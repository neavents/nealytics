#include "nea_wal.h"

#include <errno.h>
#include <limits.h>
#include <liburing.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

struct nea_wal {
    struct io_uring ring;
    unsigned char *buffer;
    size_t capacity;
    size_t length;
    size_t initialized;
    off_t offset;
    int file;
    int error;
    unsigned inflight;
    bool registered;
};

static int drain_results(nea_wal *wal, unsigned expected, int *written)
{
    unsigned seen = 0;
    unsigned received = 0;
    int failure = 0;

    while (seen < expected) {
        struct io_uring_cqe *completion = NULL;
        int result = io_uring_wait_cqe(&wal->ring, &completion);
        if (result == -EINTR) {
            continue;
        }
        if (result < 0) {
            return result;
        }

        uint64_t identity = io_uring_cqe_get_data64(completion);
        if (identity < 1 || identity > 2 || (received & (1U << identity)) != 0) {
            failure = -EIO;
        } else {
            received |= 1U << identity;
            if (identity == 1) {
                *written = completion->res;
            } else if (completion->res > 0) {
                failure = -EIO;
            }
            if (completion->res < 0) {
                bool primary = identity == 1 && completion->res != -ECANCELED;
                if (failure == 0 || failure == -ECANCELED || primary) {
                    failure = completion->res;
                }
            }
        }

        io_uring_cqe_seen(&wal->ring, completion);
        wal->inflight--;
        seen++;
    }

    return failure;
}

static int submit_write_and_sync(nea_wal *wal, size_t offset, int *written)
{
    if (io_uring_sq_space_left(&wal->ring) < 2) {
        return -EBUSY;
    }

    struct io_uring_sqe *write = io_uring_get_sqe(&wal->ring);
    struct io_uring_sqe *sync = io_uring_get_sqe(&wal->ring);
    int file = wal->registered ? 0 : wal->file;
    unsigned flags = wal->registered ? IOSQE_FIXED_FILE : 0;
    unsigned initial_head = io_uring_load_sq_head(&wal->ring);
    size_t remaining = wal->length - offset;

    io_uring_prep_write(write, file, wal->buffer + offset, (unsigned)remaining,
        (uint64_t)wal->offset + offset);
    io_uring_sqe_set_flags(write, flags | IOSQE_IO_LINK);
    io_uring_sqe_set_data64(write, 1);
    io_uring_prep_fsync(sync, file, 0);
    io_uring_sqe_set_flags(sync, flags);
    io_uring_sqe_set_data64(sync, 2);

    int result;
    do {
        result = io_uring_submit_and_wait(&wal->ring, 2);
        wal->inflight = io_uring_load_sq_head(&wal->ring) - initial_head;
        if (wal->inflight == 1) {
            return -EIO;
        }
        if (result < 0 && result != -EINTR) {
            return result;
        }
    } while (result == -EINTR);
    if (io_uring_sq_ready(&wal->ring) != 0) {
        return -EIO;
    }

    return drain_results(wal, 2, written);
}

uint32_t nea_wal_abi_version(void)
{
    return 1;
}

int nea_wal_create(int file, unsigned entries, size_t capacity, nea_wal **result)
{
    if (result == NULL) {
        return -EINVAL;
    }
    *result = NULL;
    if (file < 0 || entries < 8 || entries > 4096 || capacity == 0 || capacity > INT_MAX) {
        return -EINVAL;
    }

    nea_wal *wal = calloc(1, sizeof(*wal));
    if (wal == NULL) {
        return -ENOMEM;
    }
    wal->buffer = malloc(capacity);
    if (wal->buffer == NULL) {
        free(wal);
        return -ENOMEM;
    }

    int status = io_uring_queue_init(entries, &wal->ring, 0);
    if (status < 0) {
        free(wal->buffer);
        free(wal);
        return status;
    }

    wal->file = file;
    wal->capacity = capacity;
    wal->offset = lseek(file, 0, SEEK_END);
    if (wal->offset < 0) {
        status = -errno;
        io_uring_queue_exit(&wal->ring);
        free(wal->buffer);
        free(wal);
        return status;
    }
    wal->registered = io_uring_register_files(&wal->ring, &file, 1) == 0;
    *result = wal;
    return 0;
}

int nea_wal_append(nea_wal *wal, const void *data, size_t length)
{
    if (wal == NULL || (data == NULL && length > 0)) {
        return -EINVAL;
    }
    if (wal->error != 0) {
        return wal->error;
    }
    if (length > wal->capacity - wal->length) {
        return -E2BIG;
    }
    if (length > 0) {
        memcpy(wal->buffer + wal->length, data, length);
        wal->length += length;
        if (wal->length > wal->initialized) {
            wal->initialized = wal->length;
        }
    }
    return 0;
}

int nea_wal_flush(nea_wal *wal)
{
    if (wal == NULL) {
        return -EINVAL;
    }
    if (wal->error != 0) {
        return wal->error;
    }
    if ((uint64_t)wal->offset > INT64_MAX - wal->length) {
        wal->error = -EFBIG;
        return wal->error;
    }

    size_t completed = 0;
    do {
        int written = 0;
        int status = submit_write_and_sync(wal, completed, &written);
        if (status < 0) {
            wal->error = status;
            return status;
        }
        if ((size_t)written > wal->length - completed
            || (written == 0 && completed < wal->length)) {
            wal->error = -EIO;
            return wal->error;
        }
        completed += (size_t)written;
    } while (completed < wal->length);

    wal->offset += (off_t)wal->length;
    wal->length = 0;
    return 0;
}

int nea_wal_truncate(nea_wal *wal)
{
    if (wal == NULL) {
        return -EINVAL;
    }
    if (wal->error != 0) {
        return wal->error;
    }
    if (wal->length != 0 || wal->inflight != 0) {
        return -EBUSY;
    }
    if (ftruncate(wal->file, 0) != 0) {
        wal->error = -errno;
        return wal->error;
    }
    wal->offset = 0;
    return nea_wal_flush(wal);
}

int nea_wal_registered_file(const nea_wal *wal)
{
    return wal != NULL && wal->registered;
}

int nea_wal_destroy(nea_wal *wal)
{
    if (wal == NULL) {
        return 0;
    }
    if (wal->inflight != 0) {
        return -EBUSY;
    }
    io_uring_queue_exit(&wal->ring);
    explicit_bzero(wal->buffer, wal->initialized);
    free(wal->buffer);
    free(wal);
    return 0;
}
