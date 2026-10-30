#include "../nea_wal.c"

#include <assert.h>
#include <fcntl.h>
#include <stdio.h>

enum fault_mode {
    FAULT_NONE,
    FAULT_SHORT_WRITE,
    FAULT_ZERO_WRITE,
    FAULT_ZERO_ID,
    FAULT_BAD_ID,
    FAULT_DUPLICATE_ID,
    FAULT_OVERSIZED_RESULT,
    FAULT_POSITIVE_SYNC,
    FAULT_SYNC_ERROR,
    FAULT_REVERSED_ERRORS,
    FAULT_INTERRUPT_BEFORE,
    FAULT_INTERRUPT_AFTER,
    FAULT_NO_PROGRESS,
    FAULT_SUBMIT,
    FAULT_COMPLETION,
    FAULT_COMPLETION_INTERRUPT,
    FAULT_PARTIAL_SUBMIT
};

static enum fault_mode next_fault;
static unsigned submissions;
static unsigned held_tail;
static int completion_error;
static bool fail_allocation;
static bool fail_buffer;
static bool fail_setup;
static bool fail_registration;
static unsigned char *expected_cleared_buffer;
static size_t expected_cleared_length;
static bool cleared_buffer_returned;

int __real_io_uring_submit_and_wait(struct io_uring *ring, unsigned wait_nr);
int __real___io_uring_get_cqe(struct io_uring *ring, struct io_uring_cqe **completion,
    unsigned submit, unsigned wait_nr, sigset_t *signals);
int __real_io_uring_queue_init(unsigned entries, struct io_uring *ring, unsigned flags);
int __real_io_uring_register_files(struct io_uring *ring, const int *files, unsigned count);
void *__real_calloc(size_t count, size_t size);
void *__real_malloc(size_t size);
void __real_free(void *pointer);

void __wrap_free(void *pointer)
{
    if (pointer == expected_cleared_buffer && expected_cleared_buffer != NULL) {
        for (size_t index = 0; index < expected_cleared_length; index++) {
            assert(expected_cleared_buffer[index] == 0);
        }
        cleared_buffer_returned = true;
        expected_cleared_buffer = NULL;
    }
    __real_free(pointer);
}

void *__wrap_calloc(size_t count, size_t size)
{
    if (fail_allocation) {
        fail_allocation = false;
        return NULL;
    }
    return __real_calloc(count, size);
}

void *__wrap_malloc(size_t size)
{
    if (fail_buffer) {
        fail_buffer = false;
        return NULL;
    }
    return __real_malloc(size);
}

int __wrap_io_uring_queue_init(unsigned entries, struct io_uring *ring, unsigned flags)
{
    return fail_setup ? -EPERM : __real_io_uring_queue_init(entries, ring, flags);
}

int __wrap_io_uring_register_files(struct io_uring *ring, const int *files, unsigned count)
{
    return fail_registration ? -EOPNOTSUPP : __real_io_uring_register_files(ring, files, count);
}

int __wrap___io_uring_get_cqe(struct io_uring *ring, struct io_uring_cqe **completion,
    unsigned submit, unsigned wait_nr, sigset_t *signals)
{
    if (completion_error != 0) {
        int error = completion_error;
        completion_error = 0;
        if (error == -EINTR) {
            *ring->cq.ktail = held_tail;
        }
        return error;
    }
    return __real___io_uring_get_cqe(ring, completion, submit, wait_nr, signals);
}

int __wrap_io_uring_submit_and_wait(struct io_uring *ring, unsigned wait_nr)
{
    enum fault_mode fault = next_fault;
    next_fault = FAULT_NONE;
    submissions++;
    if (fault == FAULT_INTERRUPT_BEFORE) {
        return -EINTR;
    }
    if (fault == FAULT_NO_PROGRESS) {
        return 0;
    }
    if (fault == FAULT_SUBMIT) {
        return -EPERM;
    }
    if (fault == FAULT_PARTIAL_SUBMIT) {
        (*ring->sq.khead)++;
        return 1;
    }

    unsigned first = (ring->sq.sqe_tail - 2) & ring->sq.ring_mask;
    unsigned second = (ring->sq.sqe_tail - 1) & ring->sq.ring_mask;
    struct io_uring_sqe *write = &ring->sq.sqes[first];
    if (fault == FAULT_SHORT_WRITE) {
        write->len /= 2;
    } else if (fault == FAULT_ZERO_WRITE) {
        write->len = 0;
    } else if (fault == FAULT_BAD_ID) {
        write->user_data = 3;
    } else if (fault == FAULT_ZERO_ID) {
        write->user_data = 0;
    } else if (fault == FAULT_DUPLICATE_ID) {
        ring->sq.sqes[second].user_data = 1;
    } else if (fault == FAULT_SYNC_ERROR) {
        ring->sq.sqes[second].fsync_flags = UINT_MAX;
    }

    int status = __real_io_uring_submit_and_wait(ring, wait_nr);
    if (status < 0) {
        return status;
    }
    if (fault == FAULT_INTERRUPT_AFTER) {
        return -EINTR;
    }
    if (fault == FAULT_COMPLETION || fault == FAULT_COMPLETION_INTERRUPT) {
        held_tail = *ring->cq.ktail;
        *ring->cq.ktail = *ring->cq.khead;
        completion_error = fault == FAULT_COMPLETION ? -EIO : -EINTR;
    }
    unsigned head;
    struct io_uring_cqe *completion;
    io_uring_for_each_cqe(ring, head, completion) {
        if (fault == FAULT_OVERSIZED_RESULT && completion->user_data == 1) {
            completion->res++;
        }
        if (fault == FAULT_POSITIVE_SYNC && completion->user_data == 2) {
            completion->res = 1;
        }
        if (fault == FAULT_REVERSED_ERRORS) {
            completion->res = completion->user_data == 1 ? -ENOSPC : -EIO;
        }
    }
    if (fault == FAULT_REVERSED_ERRORS) {
        unsigned index = *ring->cq.khead;
        struct io_uring_cqe *one = &ring->cq.cqes[index & ring->cq.ring_mask];
        struct io_uring_cqe *two = &ring->cq.cqes[(index + 1) & ring->cq.ring_mask];
        if (one->user_data == 1) {
            struct io_uring_cqe temporary = *one;
            *one = *two;
            *two = temporary;
        }
    }
    return status;
}

static int temporary_file(void)
{
    char path[] = "/tmp/nealytics-native-tests-XXXXXX";
    int file = mkstemp(path);
    assert(file >= 0);
    assert(unlink(path) == 0);
    return file;
}

static nea_wal *create_wal(int file)
{
    nea_wal *wal = NULL;
    assert(nea_wal_create(file, 8, 4096, &wal) == 0);
    assert(wal != NULL);
    submissions = 0;
    return wal;
}

static void assert_contents(int file, const char *expected)
{
    char bytes[128] = {0};
    ssize_t length = pread(file, bytes, sizeof(bytes), 0);
    assert(length >= 0);
    assert((size_t)length == strlen(expected));
    assert(memcmp(bytes, expected, (size_t)length) == 0);
}

static void validation_and_cleanup(void)
{
    nea_wal *wal = (nea_wal *)(uintptr_t)1;
    int file = temporary_file();
    assert(nea_wal_abi_version() == 1);
    assert(nea_wal_create(file, 8, 4096, NULL) == -EINVAL);
    assert(nea_wal_create(-1, 8, 4096, &wal) == -EINVAL && wal == NULL);
    assert(nea_wal_create(file, 7, 4096, &wal) == -EINVAL);
    assert(nea_wal_create(file, 4097, 4096, &wal) == -EINVAL);
    assert(nea_wal_create(file, 8, 0, &wal) == -EINVAL);
    assert(nea_wal_create(file, 8, (size_t)INT_MAX + 1, &wal) == -EINVAL);
    assert(nea_wal_append(NULL, "x", 1) == -EINVAL);
    assert(nea_wal_flush(NULL) == -EINVAL);
    assert(nea_wal_truncate(NULL) == -EINVAL);
    assert(nea_wal_registered_file(NULL) == 0);
    assert(nea_wal_destroy(NULL) == 0);

    fail_allocation = true;
    assert(nea_wal_create(file, 8, 4096, &wal) == -ENOMEM && wal == NULL);
    fail_buffer = true;
    assert(nea_wal_create(file, 8, 4096, &wal) == -ENOMEM && wal == NULL);
    fail_setup = true;
    assert(nea_wal_create(file, 8, 4096, &wal) == -EPERM && wal == NULL);
    fail_setup = false;
    assert(nea_wal_create(999999, 8, 4096, &wal) == -EBADF && wal == NULL);
    close(file);
}

static void framing_capacity_and_truncation(void)
{
    int file = temporary_file();
    nea_wal *wal = create_wal(file);
    char first[] = "first\n";
    assert(nea_wal_registered_file(wal) == 1);
    assert(nea_wal_append(wal, NULL, 0) == 0);
    assert(nea_wal_append(wal, NULL, 1) == -EINVAL);
    assert(nea_wal_append(wal, first, sizeof(first) - 1) == 0);
    memset(first, 0, sizeof(first));
    assert(nea_wal_append(wal, "x", 4097) == -E2BIG);
    assert(nea_wal_truncate(wal) == -EBUSY);
    assert_contents(file, "");
    assert(nea_wal_flush(wal) == 0);
    assert_contents(file, "first\n");
    assert(nea_wal_append(wal, "second\n", 7) == 0);
    assert(nea_wal_flush(wal) == 0);
    assert_contents(file, "first\nsecond\n");
    assert(nea_wal_truncate(wal) == 0);
    assert_contents(file, "");
    assert(nea_wal_append(wal, "new\n", 4) == 0);
    assert(nea_wal_flush(wal) == 0);
    assert_contents(file, "new\n");
    assert(wal->initialized == 7);
    expected_cleared_buffer = wal->buffer;
    expected_cleared_length = wal->initialized;
    assert(nea_wal_destroy(wal) == 0);
    assert(cleared_buffer_returned);
    close(file);
}

static void recoverable_submission_fault(enum fault_mode fault)
{
    int file = temporary_file();
    nea_wal *wal = create_wal(file);
    assert(nea_wal_append(wal, "abcdefgh", 8) == 0);
    next_fault = fault;
    assert(nea_wal_flush(wal) == 0);
    assert(submissions == (fault == FAULT_COMPLETION_INTERRUPT ? 1U : 2U));
    assert_contents(file, "abcdefgh");
    assert(wal->inflight == 0);
    assert(nea_wal_destroy(wal) == 0);
    close(file);
}

static void terminal_submission_fault(enum fault_mode fault, int expected)
{
    int file = temporary_file();
    nea_wal *wal = create_wal(file);
    assert(nea_wal_append(wal, "abcdefgh", 8) == 0);
    next_fault = fault;
    int status = nea_wal_flush(wal);
    if (status != expected) {
        fprintf(stderr, "fault=%d expected=%d actual=%d\n", fault, expected, status);
    }
    assert(status == expected);
    assert(nea_wal_flush(wal) == expected);
    assert(nea_wal_append(wal, "x", 1) == expected);
    assert(nea_wal_truncate(wal) == expected);
    assert(nea_wal_destroy(wal) == 0);
    close(file);
}

static void completion_failure_retains_kernel_ownership(void)
{
    int file = temporary_file();
    nea_wal *wal = create_wal(file);
    assert(nea_wal_append(wal, "pending", 7) == 0);
    next_fault = FAULT_COMPLETION;
    assert(nea_wal_flush(wal) == -EIO);
    assert(wal->inflight == 2);
    assert(nea_wal_destroy(wal) == -EBUSY);
    assert(nea_wal_truncate(wal) == -EIO);
    *wal->ring.cq.ktail = held_tail;
    int written = 0;
    assert(drain_results(wal, 2, &written) == 0);
    assert(written == 7);
    assert(wal->inflight == 0);
    assert(nea_wal_destroy(wal) == 0);
    close(file);
}

static void offset_and_file_errors(void)
{
    int file = temporary_file();
    nea_wal *wal = create_wal(file);
    assert(nea_wal_append(wal, "x", 1) == 0);
    wal->offset = INT64_MAX;
    assert(nea_wal_flush(wal) == -EFBIG);
    assert(nea_wal_destroy(wal) == 0);
    close(file);

    file = temporary_file();
    wal = create_wal(file);
    assert(close(file) == 0);
    assert(nea_wal_truncate(wal) == -EBADF);
    assert(nea_wal_destroy(wal) == 0);
}

static void registration_fallback_and_queue_capacity(void)
{
    int file = temporary_file();
    fail_registration = true;
    nea_wal *wal = create_wal(file);
    fail_registration = false;
    assert(nea_wal_registered_file(wal) == 0);
    assert(nea_wal_append(wal, "plain", 5) == 0);
    assert(nea_wal_flush(wal) == 0);
    assert_contents(file, "plain");
    wal->inflight = 1;
    assert(nea_wal_truncate(wal) == -EBUSY);
    wal->inflight = 0;
    for (unsigned index = 0; index < 7; index++) {
        assert(io_uring_get_sqe(&wal->ring) != NULL);
    }
    int written = 0;
    assert(submit_write_and_sync(wal, 0, &written) == -EBUSY);
    assert(nea_wal_destroy(wal) == 0);
    close(file);
}

static void disk_failure_and_partial_submission(void)
{
    int file = open("/dev/full", O_RDWR);
    assert(file >= 0);
    nea_wal *wal = create_wal(file);
    assert(nea_wal_append(wal, "full", 4) == 0);
    assert(nea_wal_flush(wal) == -ENOSPC);
    assert(wal->inflight == 0);
    assert(nea_wal_destroy(wal) == 0);
    close(file);

    file = temporary_file();
    wal = create_wal(file);
    assert(nea_wal_append(wal, "pending", 7) == 0);
    unsigned head = *wal->ring.sq.khead;
    next_fault = FAULT_PARTIAL_SUBMIT;
    assert(nea_wal_flush(wal) == -EIO);
    assert(wal->inflight == 1);
    assert(nea_wal_destroy(wal) == -EBUSY);
    *wal->ring.sq.khead = head;
    wal->inflight = 0;
    assert_contents(file, "");
    assert(nea_wal_destroy(wal) == 0);
    close(file);
}

int main(void)
{
    validation_and_cleanup();
    framing_capacity_and_truncation();
    recoverable_submission_fault(FAULT_SHORT_WRITE);
    recoverable_submission_fault(FAULT_INTERRUPT_BEFORE);
    recoverable_submission_fault(FAULT_INTERRUPT_AFTER);
    recoverable_submission_fault(FAULT_COMPLETION_INTERRUPT);
    terminal_submission_fault(FAULT_ZERO_WRITE, -EIO);
    terminal_submission_fault(FAULT_BAD_ID, -EIO);
    terminal_submission_fault(FAULT_ZERO_ID, -EIO);
    terminal_submission_fault(FAULT_DUPLICATE_ID, -EIO);
    terminal_submission_fault(FAULT_OVERSIZED_RESULT, -EIO);
    terminal_submission_fault(FAULT_POSITIVE_SYNC, -EIO);
    terminal_submission_fault(FAULT_SYNC_ERROR, -EINVAL);
    terminal_submission_fault(FAULT_REVERSED_ERRORS, -ENOSPC);
    terminal_submission_fault(FAULT_NO_PROGRESS, -EIO);
    terminal_submission_fault(FAULT_SUBMIT, -EPERM);
    completion_failure_retains_kernel_ownership();
    offset_and_file_errors();
    registration_fallback_and_queue_capacity();
    disk_failure_and_partial_submission();
    puts("Native WAL framing, capacity, short-write, interruption, completion, cleanup and file-fault checks passed.");
    return 0;
}
