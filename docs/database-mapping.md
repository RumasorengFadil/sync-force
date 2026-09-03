# Database Mapping

## Prinsip mapping

Setiap sync job memiliki satu source table, satu target table, dan satu atau beberapa column mapping. Nama tabel dan kolom dipilih dari `information_schema` melalui UI; aplikasi tidak mengandalkan input SQL bebas.

## Membuat mapping

1. Simpan dan uji satu connection dengan role `Source` dan satu dengan role `Target`.
2. Buat job dan pilih kedua connection tersebut.
3. Klik **Load schema**, lalu pilih source table dan target table.
4. Klik **Auto-suggest mapping** untuk memasangkan nama kolom serupa, atau tambahkan mapping secara manual.
5. Tandai semua kolom yang membentuk primary key. Untuk key gabungan, tandai setiap komponennya.
6. Pilih timestamp source untuk mode Incremental.

## Persyaratan mode Incremental

- Primary key target harus memiliki primary key atau unique constraint yang cocok dengan mapping.
- Timestamp column harus ada di source dan bertipe `timestamp with time zone` atau `timestamp without time zone`.
- Kolom mapping source dan target harus memiliki tipe PostgreSQL kompatibel. Implementasi saat ini memvalidasi `information_schema.columns.data_type` sebelum binary COPY.
- Timestamp tidak harus dipetakan ke target, tetapi disarankan jika target juga memerlukan nilai audit tersebut.

Contoh:

| Source | Target | Primary key |
| --- | --- | --- |
| `customer_id` | `id` | Ya |
| `full_name` | `name` | Tidak |
| `updated_at` | `updated_at` | Tidak |

Dengan mapping tersebut, source `crm.customers` dapat disinkronkan ke target `reporting.customers` meskipun nama primary key dan nama kolom tampilan berbeda.

## Persyaratan mode Truncate & Reload

- Semua kolom target yang wajib terisi harus memiliki mapping atau default yang valid.
- Tabel target tidak boleh direferensikan foreign key dari tabel lain.
- Akun target membutuhkan hak untuk membuat dan menghapus staging table serta mengubah nama tabel.
- Guard rail harus dikonfigurasi; mode ini tidak melewati pemeriksaan source kosong/stabil.

## Perubahan schema

Worker memvalidasi mapping sebelum setiap run. Jika kolom dihapus, diganti nama, atau tipe berubah, run gagal cepat dan tidak memulai COPY. Perbarui mapping dari UI setelah perubahan schema disetujui.
