# Sync Flow

## Alur setiap job

```text
Scheduler
  -> load job + connections dari SQLite
  -> validasi schema dan column mapping
  -> guard rail (row count + quiescence)
  -> extract dari Source
  -> binary COPY ke staging di Target
  -> upsert atau rename-swap dalam transaksi
  -> commit Target
  -> tulis sync_history + checkpoint
```

## 1. Scheduler

Worker mengevaluasi `schedule_cron` setiap 30 detik menggunakan format cron lima kolom (`menit jam hari-bulan bulan hari-minggu`). Contoh `0 2 * * *` menjalankan job setiap hari pukul 02:00 waktu lokal server.

## 2. Validasi

Sebelum transfer, worker memastikan source table dan target table masih ada, seluruh kolom mapping masih ada, tipe kolom source dan target kompatibel untuk binary COPY, dan timestamp mode incremental tetap bertipe timestamp.

## 3. Guard rail

Worker membaca `COUNT(*)` pada source. Run dilewati jika:

- count lebih kecil daripada `min_expected_row_count`;
- count turun melebihi `max_drop_percentage_threshold` dibanding source count pada run sukses terakhir; atau
- count kedua setelah `stability_check_delay_seconds` berbeda dari count pertama.

Status `SkippedUnstable` ditulis ke histori. Target tidak disentuh.

## 4. Incremental

Untuk mode incremental, query source menggunakan:

```sql
WHERE timestamp_column > checkpoint
ORDER BY timestamp_column ASC
```

Data ditulis ke temporary staging table melalui `NpgsqlBinaryImporter`. Setelah COPY selesai, worker menjalankan `INSERT ... ON CONFLICT (...) DO UPDATE` pada tabel target dalam transaksi yang sama. Nilai timestamp terakhir hanya disimpan sebagai checkpoint setelah commit sukses.

Pastikan kolom timestamp cukup presisi dan tidak ada baris baru yang dapat muncul dengan timestamp persis sama dengan checkpoint. Bila hal ini mungkin terjadi, gunakan timestamp yang monotonik atau sesuaikan desain source.

## 5. Truncate & Reload

Worker membuat staging table permanen yang memiliki struktur target, melakukan COPY penuh, lalu dalam satu transaksi:

1. Mengubah nama target menjadi backup sementara.
2. Mengubah nama staging menjadi nama target semula.
3. Menghapus backup.

Pengguna tidak melihat window tabel kosong. Worker menolak mode ini saat target mempunyai incoming foreign key, karena rename-swap dapat membuat referensi relasional menjadi tidak aman.

## 6. Kegagalan

Jika COPY, upsert, atau swap gagal, transaksi dibatalkan dan target tetap pada keadaan sebelumnya. Worker mencatat `Failed`, kemudian retry sesuai policy. Checkpoint sebelumnya tetap berlaku.
