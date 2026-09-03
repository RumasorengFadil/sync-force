using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using SyncForge.Core.Domain;
using SyncForge.Core.Postgres;
using SyncForge.Core.Storage;

namespace SyncForge.Desktop;

public partial class MainWindow : Window
{
    private readonly IConfigStore _configStore;
    private readonly IPostgresConnectionFactory _connectionFactory;
    private readonly IPostgresSchemaReader _schemaReader;
    private DbConnectionConfiguration? _selectedConnection;
    private SyncJob? _selectedJob;

    public ObservableCollection<DbConnectionConfiguration> Connections { get; } = [];
    public ObservableCollection<DbConnectionConfiguration> SourceConnections { get; } = [];
    public ObservableCollection<DbConnectionConfiguration> TargetConnections { get; } = [];
    public ObservableCollection<SyncJob> Jobs { get; } = [];
    public ObservableCollection<DatabaseTable> SourceTables { get; } = [];
    public ObservableCollection<DatabaseTable> TargetTables { get; } = [];
    public ObservableCollection<DatabaseColumn> SourceColumns { get; } = [];
    public ObservableCollection<DatabaseColumn> TargetColumns { get; } = [];
    public ObservableCollection<MappingRow> Mappings { get; } = [];
    public ObservableCollection<SyncHistoryRecord> History { get; } = [];

    public MainWindow(IConfigStore configStore, IPostgresConnectionFactory connectionFactory, IPostgresSchemaReader schemaReader)
    {
        _configStore = configStore;
        _connectionFactory = connectionFactory;
        _schemaReader = schemaReader;
        InitializeComponent();
        DataContext = this;
        Loaded += async (_, _) => await RefreshAllAsync();
        NewConnectionForm();
        NewJobForm();
    }

    private async Task RefreshAllAsync()
    {
        try
        {
            SetStatus("Loading configuration...");
            var connections = await _configStore.GetConnectionsAsync();
            var jobs = await _configStore.GetJobsAsync();
            var history = await _configStore.GetHistoryAsync();
            Replace(Connections, connections);
            Replace(SourceConnections, connections.Where(item => item.Role == ConnectionRole.Source));
            Replace(TargetConnections, connections.Where(item => item.Role == ConnectionRole.Target));
            Replace(Jobs, jobs);
            Replace(History, history);
            ConnectionsCountText.Text = connections.Count.ToString(CultureInfo.InvariantCulture);
            EnabledJobsCountText.Text = jobs.Count(item => item.Enabled).ToString(CultureInfo.InvariantCulture);
            SuccessCountText.Text = history.Count(item => item.Status == SyncRunStatus.Success).ToString(CultureInfo.InvariantCulture);
            SkippedCountText.Text = history.Count(item => item.Status == SyncRunStatus.SkippedUnstable).ToString(CultureInfo.InvariantCulture);
            SetStatus($"Ready - {connections.Count} connection(s), {jobs.Count} job(s).");
        }
        catch (Exception exception)
        {
            ShowError("Configuration tidak dapat dimuat", exception);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    private void NewConnection_Click(object sender, RoutedEventArgs e) => NewConnectionForm();

    private void NewConnectionForm()
    {
        _selectedConnection = null;
        ConnectionsGrid.SelectedItem = null;
        ConnectionNameBox.Text = string.Empty;
        ConnectionRoleBox.SelectedIndex = 0;
        ConnectionHostBox.Text = string.Empty;
        ConnectionPortBox.Text = "5432";
        ConnectionDatabaseBox.Text = string.Empty;
        ConnectionUsernameBox.Text = string.Empty;
        ConnectionPasswordBox.Password = string.Empty;
    }

    private void ConnectionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectionsGrid.SelectedItem is not DbConnectionConfiguration connection)
        {
            return;
        }

        _selectedConnection = connection;
        ConnectionNameBox.Text = connection.Name;
        ConnectionRoleBox.SelectedIndex = connection.Role == ConnectionRole.Source ? 0 : 1;
        ConnectionHostBox.Text = connection.Host;
        ConnectionPortBox.Text = connection.Port.ToString(CultureInfo.InvariantCulture);
        ConnectionDatabaseBox.Text = connection.Database;
        ConnectionUsernameBox.Text = connection.Username;
        ConnectionPasswordBox.Password = connection.Password;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var configuration = ReadConnectionForm();
            SetStatus($"Testing {configuration.Name}...");
            await _connectionFactory.TestConnectionAsync(configuration);
            SetStatus($"Connection '{configuration.Name}' berhasil.");
            MessageBox.Show("Koneksi PostgreSQL berhasil.", "SyncForge", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("Koneksi gagal", exception);
        }
    }

    private async void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var configuration = ReadConnectionForm();
            var id = await _configStore.SaveConnectionAsync(configuration);
            _selectedConnection = await _configStore.GetConnectionAsync(id);
            await RefreshAllAsync();
            SetStatus($"Connection '{configuration.Name}' tersimpan.");
        }
        catch (Exception exception)
        {
            ShowError("Connection tidak dapat disimpan", exception);
        }
    }

    private async void DeleteConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedConnection is null)
        {
            return;
        }

        if (MessageBox.Show($"Hapus koneksi '{_selectedConnection.Name}'? Job yang masih memakainya harus dihapus terlebih dahulu.", "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _configStore.DeleteConnectionAsync(_selectedConnection.Id);
            NewConnectionForm();
            await RefreshAllAsync();
        }
        catch (SqliteException exception)
        {
            ShowError("Connection masih digunakan oleh job", exception);
        }
    }

    private DbConnectionConfiguration ReadConnectionForm()
    {
        if (!int.TryParse(ConnectionPortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            throw new ArgumentException("Port harus berupa angka.");
        }

        return new DbConnectionConfiguration
        {
            Id = _selectedConnection?.Id ?? 0,
            Name = ConnectionNameBox.Text,
            Role = ConnectionRoleBox.SelectedIndex == 1 ? ConnectionRole.Target : ConnectionRole.Source,
            Host = ConnectionHostBox.Text,
            Port = port,
            Database = ConnectionDatabaseBox.Text,
            Username = ConnectionUsernameBox.Text,
            Password = ConnectionPasswordBox.Password,
            CreatedAt = _selectedConnection?.CreatedAt ?? DateTimeOffset.UtcNow
        };
    }

    private void NewJob_Click(object sender, RoutedEventArgs e) => NewJobForm();

    private void NewJobForm()
    {
        _selectedJob = null;
        JobsList.SelectedItem = null;
        JobNameBox.Text = string.Empty;
        JobModeBox.SelectedIndex = 0;
        JobEnabledBox.IsChecked = true;
        SourceConnectionBox.SelectedItem = null;
        TargetConnectionBox.SelectedItem = null;
        SourceTables.Clear();
        TargetTables.Clear();
        SourceColumns.Clear();
        TargetColumns.Clear();
        SourceTableBox.SelectedItem = null;
        TargetTableBox.SelectedItem = null;
        TimestampColumnBox.SelectedItem = null;
        MinimumRowsBox.Text = "0";
        MaximumDropBox.Text = "30";
        ScheduleCronBox.Text = "0 2 * * *";
        StabilityCheckBox.IsChecked = true;
        StabilityDelayBox.Text = "15";
        Mappings.Clear();
    }

    private async void JobsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JobsList.SelectedItem is not SyncJob job)
        {
            return;
        }

        try
        {
            _selectedJob = job;
            JobNameBox.Text = job.Name;
            JobModeBox.SelectedIndex = job.Mode == SyncMode.Incremental ? 0 : 1;
            JobEnabledBox.IsChecked = job.Enabled;
            SourceConnectionBox.SelectedValue = job.ConnectionSourceId;
            TargetConnectionBox.SelectedValue = job.ConnectionTargetId;
            MinimumRowsBox.Text = job.MinExpectedRowCount.ToString(CultureInfo.InvariantCulture);
            MaximumDropBox.Text = job.MaxDropPercentageThreshold.ToString(CultureInfo.InvariantCulture);
            ScheduleCronBox.Text = job.ScheduleCron;
            StabilityCheckBox.IsChecked = job.StabilityCheckEnabled;
            StabilityDelayBox.Text = job.StabilityCheckDelaySeconds.ToString(CultureInfo.InvariantCulture);
            await LoadSchemaAsync(job.SourceTable, job.TargetTable);
            TimestampColumnBox.SelectedValue = job.TimestampColumn;
            Replace(Mappings, job.ColumnMappings.Select(mapping => new MappingRow(mapping.SourceColumn, mapping.TargetColumn, mapping.IsPrimaryKey)));
            SetStatus($"Editing job '{job.Name}'.");
        }
        catch (Exception exception)
        {
            ShowError("Job tidak dapat dimuat", exception);
        }
    }

    private void SourceConnectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => SetStatus("Pilih Load schema untuk membaca tabel source.");

    private void TargetConnectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => SetStatus("Pilih Load schema untuk membaca tabel target.");

    private async void LoadSchema_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadSchemaAsync();
        }
        catch (Exception exception)
        {
            ShowError("Schema tidak dapat dibaca", exception);
        }
    }

    private async Task LoadSchemaAsync(string? selectedSourceTable = null, string? selectedTargetTable = null)
    {
        var source = SelectedSourceConnection();
        var target = SelectedTargetConnection();
        if (source is null || target is null)
        {
            throw new InvalidOperationException("Pilih satu koneksi Source dan satu koneksi Target terlebih dahulu.");
        }

        SetStatus("Reading database schema...");
        var sourceTablesTask = _schemaReader.GetTablesAsync(source);
        var targetTablesTask = _schemaReader.GetTablesAsync(target);
        await Task.WhenAll(sourceTablesTask, targetTablesTask);
        Replace(SourceTables, sourceTablesTask.Result);
        Replace(TargetTables, targetTablesTask.Result);
        SourceTableBox.SelectedValue = selectedSourceTable ?? SourceTableBox.SelectedValue;
        TargetTableBox.SelectedValue = selectedTargetTable ?? TargetTableBox.SelectedValue;
        await LoadColumnsAsync();
        SetStatus($"Schema loaded: {SourceTables.Count} source table(s), {TargetTables.Count} target table(s).");
    }

    private async void SourceTableBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadColumnsAsync();

    private async void TargetTableBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadColumnsAsync();

    private async Task LoadColumnsAsync()
    {
        var source = SelectedSourceConnection();
        var target = SelectedTargetConnection();
        var sourceTable = SourceTableBox.SelectedValue as string;
        var targetTable = TargetTableBox.SelectedValue as string;
        if (source is null || target is null || string.IsNullOrWhiteSpace(sourceTable) || string.IsNullOrWhiteSpace(targetTable))
        {
            return;
        }

        var sourceColumnsTask = _schemaReader.GetColumnsAsync(source, sourceTable);
        var targetColumnsTask = _schemaReader.GetColumnsAsync(target, targetTable);
        await Task.WhenAll(sourceColumnsTask, targetColumnsTask);
        Replace(SourceColumns, sourceColumnsTask.Result);
        Replace(TargetColumns, targetColumnsTask.Result);
    }

    private void AutoMap_Click(object sender, RoutedEventArgs e)
    {
        if (SourceColumns.Count == 0 || TargetColumns.Count == 0)
        {
            MessageBox.Show("Load schema dan pilih kedua tabel terlebih dahulu.", "SyncForge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targets = TargetColumns.ToDictionary(column => Normalize(column.Name), StringComparer.Ordinal);
        var suggestions = SourceColumns
            .Where(column => targets.ContainsKey(Normalize(column.Name)))
            .Select(column => new MappingRow(column.Name, targets[Normalize(column.Name)].Name,
                string.Equals(Normalize(column.Name), "id", StringComparison.Ordinal)))
            .ToArray();
        Replace(Mappings, suggestions);
        SetStatus($"{suggestions.Length} mapping suggestion(s) dibuat. Tandai primary key sebelum menyimpan.");
    }

    private void AddMappingRow_Click(object sender, RoutedEventArgs e) => Mappings.Add(new MappingRow());

    private void ClearMappings_Click(object sender, RoutedEventArgs e) => Mappings.Clear();

    private void JobModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimestampColumnBox is not null)
        {
            TimestampColumnBox.IsEnabled = JobModeBox.SelectedIndex == 0;
        }
    }

    private async void SaveJob_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var job = ReadJobForm(out var mappings);
            var id = await _configStore.SaveJobAsync(job, mappings);
            await RefreshAllAsync();
            _selectedJob = await _configStore.GetJobAsync(id);
            SetStatus($"Job '{job.Name}' tersimpan.");
        }
        catch (Exception exception)
        {
            ShowError("Job tidak dapat disimpan", exception);
        }
    }

    private async void DeleteJob_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedJob is null)
        {
            return;
        }

        if (MessageBox.Show($"Hapus job '{_selectedJob.Name}' beserta histori run-nya?", "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _configStore.DeleteJobAsync(_selectedJob.Id);
        NewJobForm();
        await RefreshAllAsync();
    }

    private SyncJob ReadJobForm(out IReadOnlyCollection<ColumnMapping> mappings)
    {
        if (!long.TryParse(MinimumRowsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minimumRows) || minimumRows < 0 ||
            !decimal.TryParse(MaximumDropBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var maximumDrop) || maximumDrop is < 0 or > 100 ||
            !int.TryParse(StabilityDelayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stabilityDelay) || stabilityDelay is < 1 or > 300)
        {
            throw new ArgumentException("Periksa minimum rows, max drop, dan stability delay.");
        }

        var source = SelectedSourceConnection() ?? throw new ArgumentException("Koneksi Source belum dipilih.");
        var target = SelectedTargetConnection() ?? throw new ArgumentException("Koneksi Target belum dipilih.");
        var sourceTable = SourceTableBox.SelectedValue as string ?? throw new ArgumentException("Tabel Source belum dipilih.");
        var targetTable = TargetTableBox.SelectedValue as string ?? throw new ArgumentException("Tabel Target belum dipilih.");
        var rows = Mappings
            .Where(row => !string.IsNullOrWhiteSpace(row.SourceColumn) && !string.IsNullOrWhiteSpace(row.TargetColumn))
            .ToArray();
        mappings = rows.Select(row => new ColumnMapping
        {
            SourceColumn = row.SourceColumn!, TargetColumn = row.TargetColumn!, IsPrimaryKey = row.IsPrimaryKey
        }).ToArray();
        var mode = JobModeBox.SelectedIndex == 1 ? SyncMode.TruncateReload : SyncMode.Incremental;
        var sourcePrimaryKeys = rows.Where(row => row.IsPrimaryKey).Select(row => row.SourceColumn).Where(value => value is not null).Cast<string>();
        var targetPrimaryKeys = rows.Where(row => row.IsPrimaryKey).Select(row => row.TargetColumn).Where(value => value is not null).Cast<string>();
        return new SyncJob
        {
            Id = _selectedJob?.Id ?? 0,
            Name = JobNameBox.Text,
            ConnectionSourceId = source.Id,
            ConnectionTargetId = target.Id,
            SourceTable = sourceTable,
            TargetTable = targetTable,
            Mode = mode,
            TimestampColumn = mode == SyncMode.Incremental ? TimestampColumnBox.SelectedValue as string : null,
            SourcePrimaryKey = string.Join(",", sourcePrimaryKeys),
            TargetPrimaryKey = string.Join(",", targetPrimaryKeys),
            MinExpectedRowCount = minimumRows,
            MaxDropPercentageThreshold = maximumDrop,
            StabilityCheckEnabled = StabilityCheckBox.IsChecked == true,
            StabilityCheckDelaySeconds = stabilityDelay,
            ScheduleCron = ScheduleCronBox.Text,
            Enabled = JobEnabledBox.IsChecked == true,
            CreatedAt = _selectedJob?.CreatedAt ?? DateTimeOffset.UtcNow
        };
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        Replace(History, await _configStore.GetHistoryAsync());
        SetStatus("History refreshed.");
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is TabControl tabs && tabs.SelectedIndex == 3)
        {
            Replace(History, await _configStore.GetHistoryAsync());
        }
    }

    private DbConnectionConfiguration? SelectedSourceConnection() => SourceConnectionBox.SelectedItem as DbConnectionConfiguration;

    private DbConnectionConfiguration? SelectedTargetConnection() => TargetConnectionBox.SelectedItem as DbConnectionConfiguration;

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        destination.Clear();
        foreach (var item in source)
        {
            destination.Add(item);
        }
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private static void ShowError(string title, Exception exception) =>
        MessageBox.Show($"{title}.\n\n{exception.Message}", "SyncForge", MessageBoxButton.OK, MessageBoxImage.Error);
}

public sealed class MappingRow(string? sourceColumn = null, string? targetColumn = null, bool isPrimaryKey = false)
{
    public string? SourceColumn { get; set; } = sourceColumn;
    public string? TargetColumn { get; set; } = targetColumn;
    public bool IsPrimaryKey { get; set; } = isPrimaryKey;
}
