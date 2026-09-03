# Architecture

## Gambaran sistem

```text
WPF Desktop UI ──writes──> SQLite Config Store <──reads── Worker Service
       │                                              │
       └── test connection / schema discovery         ├── Cron scheduler
                                                       ├── Guard rail
PostgreSQL Source <────── streaming reader ───────────┤
PostgreSQL Target <──── binary COPY / transaction ────┘
```

## Project solution

| Project | Peran |
| --- | --- |
| `SyncForge.Core` | Domain model, SQLite repository, DPAPI protector, PostgreSQL schema reader, guard rail, dan sync engine. |
| `SyncForge.Desktop` | Aplikasi WPF companion untuk administrasi dan monitoring. |
| `SyncForge.Worker` | Host Windows Service, scheduler Cronos, retry Polly, dan log Serilog. |
| `SyncForge.Core.Tests` | Unit test logika guard rail. |

## Penyimpanan konfigurasi

Config store berada di `%ProgramData%\SyncForge\syncforge.db` dan berisi:

- `connections`: endpoint PostgreSQL dan password terenkripsi DPAPI level mesin.
- `sync_jobs`: konfigurasi source/target table, mode, jadwal, guard rail, dan status aktif.
- `column_mappings`: pasangan kolom source ke kolom target serta penanda primary key.
- `sync_history`: hasil semua run. Baris `success` terbaru adalah sumber kebenaran checkpoint incremental.

Folder config dibuat dengan ACL untuk administrator dan `SYSTEM` oleh installer. Jangan menyalin database SQLite ini ke mesin lain karena kredensial DPAPI tidak dapat didekripsi di mesin tersebut.

## Batas proses

UI tidak menjalankan pipeline sync. UI hanya menulis konfigurasi dan menampilkan histori. Worker membaca konfigurasi pada setiap siklus 30 detik, sehingga perubahan konfigurasi tidak memerlukan restart service.

## Logging dan retry

Worker menulis log rolling harian di `%ProgramData%\SyncForge\logs` dengan retensi 30 hari. Kegagalan transient dicoba ulang maksimal tiga kali dengan exponential backoff 2, 4, lalu 8 detik. Checkpoint tidak ditulis jika transaksi target gagal.
