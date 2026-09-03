# Deployment

## Prasyarat

- Windows 10 atau Windows 11 x64.
- .NET 8 SDK untuk build, atau .NET 8 Desktop Runtime untuk menjalankan publish framework-dependent.
- Akses jaringan dari komputer aplikasi ke PostgreSQL Source dan Target.
- Inno Setup untuk membangun installer.
- Hak administrator saat memasang Windows Service.

## Build release

Dari root repository, jalankan di PowerShell Windows:

```powershell
dotnet restore
dotnet test
dotnet build SyncForge.sln -c Release
dotnet publish src/SyncForge.Desktop -c Release -r win-x64 --self-contained false -o publish/desktop
dotnet publish src/SyncForge.Worker -c Release -r win-x64 --self-contained false -o publish/worker
```

Kemudian build `installer/SyncForge.iss` menggunakan Inno Setup Compiler:

```powershell
ISCC installer\SyncForge.iss
```

Installer dihasilkan pada `output\installer\SyncForge-Setup.exe`.

## Instalasi

1. Jalankan installer sebagai Administrator.
2. Installer memasang Desktop UI, Worker executable, dan service bernama `SyncForge Worker` dengan startup otomatis.
3. Verifikasi service:

```powershell
Get-Service -Name "SyncForge Worker"
Get-Content "$env:ProgramData\SyncForge\logs\worker-*.log" -Tail 50
```

4. Buka SyncForge dari Start Menu, lalu buat dan test connection serta sync job.

## Upgrade dan rollback

Sebelum upgrade, ekspor atau salin `%ProgramData%\SyncForge\syncforge.db` sebagai backup. Jangan menyalin backup tersebut ke mesin lain; DPAPI level mesin akan membuat password tidak dapat didekripsi di komputer tujuan.

Untuk upgrade, jalankan installer versi baru. Untuk rollback, hentikan service, pasang installer versi sebelumnya, lalu jalankan service kembali. Pastikan perubahan schema/konfigurasi tetap kompatibel dengan versi yang dikembalikan.

## Verifikasi pasca-deploy

- Service berada pada status `Running`.
- Kedua koneksi lolos **Test connection** dari UI.
- Satu job incremental test sukses dan menghasilkan histori `Success`.
- Simulasi source kosong atau berubah saat quiescence check menghasilkan `SkippedUnstable` tanpa perubahan pada target.
- Log dapat ditulis di `%ProgramData%\SyncForge\logs` dan konfigurasi tersimpan di `%ProgramData%\SyncForge`.
