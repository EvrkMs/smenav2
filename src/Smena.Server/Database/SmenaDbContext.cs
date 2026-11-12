using Microsoft.EntityFrameworkCore;
using Smena.Server.Accounting;
using Smena.Server.Banking;
using Smena.Server.Cashier;
using Smena.Server.Employees;
using Smena.Server.Notifications;
using Smena.Server.Photos;
using Smena.Server.Root;
using Smena.Server.Settings;
using Smena.Server.Shifts;

namespace Smena.Server.Database;

public sealed class SmenaDbContext(DbContextOptions<SmenaDbContext> options) : DbContext(options)
{
    public DbSet<ClubSettings> ClubSettings => Set<ClubSettings>();

    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Operation> Operations => Set<Operation>();

    public DbSet<LedgerTransaction> Transactions => Set<LedgerTransaction>();

    public DbSet<LedgerEntry> Entries => Set<LedgerEntry>();

    public DbSet<Shift> Shifts => Set<Shift>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<ReceiptChannel> ReceiptChannels => Set<ReceiptChannel>();

    public DbSet<BankReceipt> BankReceipts => Set<BankReceipt>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<RootUser> RootUsers => Set<RootUser>();

    public DbSet<PhotoSession> PhotoSessions => Set<PhotoSession>();

    public DbSet<Photo> Photos => Set<Photo>();

    /// <summary>Представление: проводки с суммой, датой и признаком отмены. Только чтение.</summary>
    public DbSet<AccountMovement> AccountMovements => Set<AccountMovement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SmenaDbContext).Assembly);
    }

    /// <summary>
    /// Единственное место, где задаётся провайдер и соглашения. Используется и
    /// при работе сервера, и инструментами EF при создании миграций — иначе
    /// миграции могли бы строиться по модели с другими именами.
    /// </summary>
    internal static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        builder
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();
    }
}
