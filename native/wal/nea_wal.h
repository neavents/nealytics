#ifndef NEA_WAL_H
#define NEA_WAL_H

#include <stddef.h>
#include <stdint.h>

#define NEA_WAL_API __attribute__((visibility("default")))

typedef struct nea_wal nea_wal;

NEA_WAL_API uint32_t nea_wal_abi_version(void);
NEA_WAL_API int nea_wal_create(int file, unsigned entries, size_t capacity, nea_wal **result);
NEA_WAL_API int nea_wal_append(nea_wal *wal, const void *data, size_t length);
NEA_WAL_API int nea_wal_flush(nea_wal *wal);
NEA_WAL_API int nea_wal_truncate(nea_wal *wal);
NEA_WAL_API int nea_wal_registered_file(const nea_wal *wal);
NEA_WAL_API int nea_wal_destroy(nea_wal *wal);

#endif
