using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Smena.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "outbox_messages_sequence",
                incrementBy: 10);

            migrationBuilder.CreateTable(
                name: "account_kinds",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    side = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_kinds", x => x.id);
                    table.CheckConstraint("ck_account_kinds_side", "side IN (1, 2)");
                });

            migrationBuilder.CreateTable(
                name: "actor_kinds",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_actor_kinds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "club_settings",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    cash_float = table.Column<int>(type: "integer", nullable: false),
                    max_employees_per_shift = table.Column<int>(type: "integer", nullable: false),
                    max_hours_per_shift = table.Column<int>(type: "integer", nullable: false),
                    min_shift_photos = table.Column<int>(type: "integer", nullable: false),
                    photo_max_age_hours = table.Column<int>(type: "integer", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    telegram_chat_id = table.Column<long>(type: "bigint", nullable: true),
                    telegram_reports_thread_id = table.Column<int>(type: "integer", nullable: true),
                    telegram_safe_thread_id = table.Column<int>(type: "integer", nullable: true),
                    telegram_expenses_thread_id = table.Column<int>(type: "integer", nullable: true),
                    telegram_salary_chat_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_club_settings", x => x.id);
                    table.CheckConstraint("ck_club_settings_cash_float", "cash_float >= 0");
                    table.CheckConstraint("ck_club_settings_max_employees", "max_employees_per_shift >= 1");
                    table.CheckConstraint("ck_club_settings_max_hours", "max_hours_per_shift >= 1");
                    table.CheckConstraint("ck_club_settings_min_photos", "min_shift_photos >= 0");
                    table.CheckConstraint("ck_club_settings_photo_age", "photo_max_age_hours >= 1");
                    table.CheckConstraint("ck_club_settings_singleton", "id = 1");
                    table.CheckConstraint("ck_club_settings_time_zone", "(timestamptz '2000-01-01 00:00:00+00' AT TIME ZONE time_zone_id) IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                    table.CheckConstraint("ck_devices_token_hash", "octet_length(token_hash) = 32");
                });

            migrationBuilder.CreateTable(
                name: "employees",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    telegram_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    telegram_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    salary_thread_id = table.Column<int>(type: "integer", nullable: true),
                    hourly_rate = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employees", x => x.id);
                    table.CheckConstraint("ck_employees_hourly_rate", "hourly_rate >= 0");
                    table.CheckConstraint("ck_employees_telegram_id", "telegram_id > 0");
                });

            migrationBuilder.CreateTable(
                name: "operation_types",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operation_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_statuses",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_statuses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "photo_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_photo_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "receipt_channels",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    delay_days = table.Column<int>(type: "integer", nullable: true),
                    fee_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt_channels", x => x.id);
                    table.CheckConstraint("ck_receipt_channels_delay", "delay_days IS NULL OR delay_days >= 0");
                    table.CheckConstraint("ck_receipt_channels_fee", "fee_percent IS NULL OR fee_percent BETWEEN 0 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "root_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    failed_logins = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    password_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_root_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "category_kinds",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    account_kind_id = table.Column<short>(type: "smallint", nullable: false),
                    direction = table.Column<short>(type: "smallint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_category_kinds", x => x.id);
                    table.CheckConstraint("ck_category_kinds_direction", "direction IS NULL OR direction IN (-1, 1)");
                    table.ForeignKey(
                        name: "fk_category_kinds_account_kinds_account_kind_id",
                        column: x => x.account_kind_id,
                        principalTable: "account_kinds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'100', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind_id = table.Column<short>(type: "smallint", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.CheckConstraint("ck_accounts_employee", "(kind_id = 4) = (employee_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_accounts_account_kind_row_kind_id",
                        column: x => x.kind_id,
                        principalTable: "account_kinds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_accounts_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_id = table.Column<short>(type: "smallint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    actor_kind_id = table.Column<short>(type: "smallint", nullable: false),
                    actor_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operations", x => x.id);
                    table.CheckConstraint("ck_operations_cancel", "(cancelled_at IS NULL) = (cancelled_by IS NULL) AND (cancelled_at IS NULL) = (cancel_reason IS NULL)");
                    table.ForeignKey(
                        name: "fk_operations_actor_kinds_actor_kind_id",
                        column: x => x.actor_kind_id,
                        principalTable: "actor_kinds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_operations_operation_type_row_type_id",
                        column: x => x.type_id,
                        principalTable: "operation_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'1000', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind_id = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    counter_account_id = table.Column<int>(type: "integer", nullable: true),
                    counter_category_id = table.Column<int>(type: "integer", nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.CheckConstraint("ck_categories_system_not_archived", "code IS NULL OR NOT is_archived");
                    table.ForeignKey(
                        name: "fk_categories_accounts_counter_account_id",
                        column: x => x.counter_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_categories_categories_counter_category_id",
                        column: x => x.counter_category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_categories_category_kind_row_kind_id",
                        column: x => x.kind_id,
                        principalTable: "category_kinds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shifts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fact_cash = table.Column<int>(type: "integer", nullable: false),
                    fact_non_cash = table.Column<int>(type: "integer", nullable: false),
                    sklad_cash = table.Column<int>(type: "integer", nullable: false),
                    sklad_non_cash = table.Column<int>(type: "integer", nullable: false),
                    langame_cash = table.Column<int>(type: "integer", nullable: false),
                    langame_non_cash = table.Column<int>(type: "integer", nullable: false),
                    fact_safe = table.Column<int>(type: "integer", nullable: false),
                    expected_safe = table.Column<int>(type: "integer", nullable: false),
                    cash_float = table.Column<int>(type: "integer", nullable: false),
                    cash_discrepancy = table.Column<int>(type: "integer", nullable: false),
                    safe_discrepancy = table.Column<int>(type: "integer", nullable: false),
                    shortage_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shifts", x => x.id);
                    table.CheckConstraint("ck_shifts_amounts", "fact_cash >= 0 AND fact_non_cash >= 0 AND sklad_cash >= 0 AND sklad_non_cash >= 0 AND langame_cash >= 0 AND langame_non_cash >= 0 AND fact_safe >= 0 AND cash_float >= 0");
                    table.ForeignKey(
                        name: "fk_shifts_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<short>(type: "smallint", nullable: false),
                    amount = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transactions", x => x.id);
                    table.CheckConstraint("ck_transactions_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_transactions_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    thread_id = table.Column<int>(type: "integer", nullable: true),
                    text = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    photos_shift_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status_id = table.Column<short>(type: "smallint", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    telegram_message_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.sequence);
                    table.CheckConstraint("ck_outbox_messages_sent", "(status_id = 2) = (sent_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_outbox_messages_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outbox_messages_outbox_status_row_status_id",
                        column: x => x.status_id,
                        principalTable: "outbox_statuses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_outbox_messages_shifts_photos_shift_id",
                        column: x => x.photos_shift_id,
                        principalTable: "shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "photos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shift_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    path = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    bytes = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_stale = table.Column<bool>(type: "boolean", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_photos", x => x.id);
                    table.CheckConstraint("ck_photos_sha256", "octet_length(sha256) = 32");
                    table.CheckConstraint("ck_photos_size", "bytes > 0 AND width > 0 AND height > 0");
                    table.ForeignKey(
                        name: "fk_photos_photo_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "photo_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_photos_shifts_shift_id",
                        column: x => x.shift_id,
                        principalTable: "shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shift_employees",
                columns: table => new
                {
                    shift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<short>(type: "smallint", nullable: false),
                    hours = table.Column<int>(type: "integer", nullable: false),
                    hourly_rate = table.Column<int>(type: "integer", nullable: false),
                    minus = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shift_employees", x => new { x.shift_id, x.employee_id });
                    table.CheckConstraint("ck_shift_employees_values", "hours >= 0 AND hourly_rate >= 0 AND minus >= 0");
                    table.ForeignKey(
                        name: "fk_shift_employees_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shift_employees_shifts_shift_id",
                        column: x => x.shift_id,
                        principalTable: "shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "bank_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<short>(type: "smallint", nullable: false),
                    delay_days = table.Column<int>(type: "integer", nullable: true),
                    fee_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    expected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expected_amount = table.Column<int>(type: "integer", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_amount = table.Column<int>(type: "integer", nullable: true),
                    confirmed_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    fee_transaction_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_receipts", x => x.id);
                    table.CheckConstraint("ck_bank_receipts_confirmation", "(confirmed_at IS NULL) = (confirmed_amount IS NULL) AND (confirmed_at IS NULL) = (confirmed_by IS NULL) AND (confirmed_at IS NOT NULL OR fee_transaction_id IS NULL)");
                    table.CheckConstraint("ck_bank_receipts_confirmed_amount", "confirmed_amount IS NULL OR confirmed_amount >= 0");
                    table.ForeignKey(
                        name: "fk_bank_receipts_receipt_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "receipt_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_receipts_transactions_fee_transaction_id",
                        column: x => x.fee_transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_bank_receipts_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<int>(type: "integer", nullable: false),
                    direction = table.Column<short>(type: "smallint", nullable: false),
                    category_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entries", x => x.id);
                    table.CheckConstraint("ck_entries_direction", "direction IN (-1, 1)");
                    table.ForeignKey(
                        name: "fk_entries_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_entries_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_entries_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "account_kinds",
                columns: new[] { "id", "code", "name", "side" },
                values: new object[,]
                {
                    { (short)1, "safe", "Сейф", (short)1 },
                    { (short)2, "bank", "Банк", (short)1 },
                    { (short)3, "expense", "Расходы", (short)1 },
                    { (short)4, "employee", "Сотрудник", (short)2 },
                    { (short)5, "revenue", "Выручка", (short)2 },
                    { (short)6, "opening", "Стартовые остатки", (short)2 }
                });

            migrationBuilder.InsertData(
                table: "actor_kinds",
                columns: new[] { "id", "code", "name" },
                values: new object[,]
                {
                    { (short)1, "cashier", "Администратор смены" },
                    { (short)2, "root", "Root" },
                    { (short)3, "system", "Система" }
                });

            migrationBuilder.InsertData(
                table: "club_settings",
                columns: new[] { "id", "cash_float", "max_employees_per_shift", "max_hours_per_shift", "min_shift_photos", "photo_max_age_hours", "telegram_chat_id", "telegram_expenses_thread_id", "telegram_reports_thread_id", "telegram_safe_thread_id", "telegram_salary_chat_id", "time_zone_id" },
                values: new object[] { (short)1, 1000, 3, 12, 3, 5, null, null, null, null, null, "Europe/Moscow" });

            migrationBuilder.InsertData(
                table: "operation_types",
                columns: new[] { "id", "code", "name" },
                values: new object[,]
                {
                    { (short)1, "shift_close", "Закрытие смены" },
                    { (short)2, "payout", "Выплата сотруднику" },
                    { (short)3, "safe_income", "Приход в сейф" },
                    { (short)4, "safe_expense", "Расход из сейфа" },
                    { (short)5, "bank_income", "Приход в банк" },
                    { (short)6, "bank_expense", "Расход с банка" },
                    { (short)7, "bank_receipt", "Подтверждение зачисления" },
                    { (short)8, "inventory", "Инвентаризация" },
                    { (short)9, "fine", "Штраф" },
                    { (short)10, "bonus", "Премия" },
                    { (short)11, "opening", "Стартовые остатки" },
                    { (short)12, "bank_adjustment", "Корректировка банка" }
                });

            migrationBuilder.InsertData(
                table: "outbox_statuses",
                columns: new[] { "id", "code", "name" },
                values: new object[,]
                {
                    { (short)1, "pending", "Ждёт отправки" },
                    { (short)2, "sent", "Отправлено" },
                    { (short)3, "failed", "Отказ Telegram" }
                });

            migrationBuilder.InsertData(
                table: "receipt_channels",
                columns: new[] { "id", "code", "delay_days", "fee_percent", "name" },
                values: new object[,]
                {
                    { (short)1, "terminal", 1, 0m, "Терминал" },
                    { (short)2, "cash_deposit", null, null, "Инкассация" }
                });

            migrationBuilder.InsertData(
                table: "accounts",
                columns: new[] { "id", "employee_id", "kind_id", "name" },
                values: new object[,]
                {
                    { 1, null, (short)1, "Сейф" },
                    { 2, null, (short)2, "Банк" },
                    { 3, null, (short)3, "Расходы" },
                    { 4, null, (short)5, "Выручка" },
                    { 5, null, (short)6, "Стартовые остатки" }
                });

            migrationBuilder.InsertData(
                table: "category_kinds",
                columns: new[] { "id", "account_kind_id", "code", "direction", "name" },
                values: new object[,]
                {
                    { (short)1, (short)1, "safe_income", (short)1, "Приходы сейфа" },
                    { (short)2, (short)1, "safe_expense", (short)-1, "Расходы сейфа" },
                    { (short)3, (short)2, "bank_income", (short)1, "Приходы банка" },
                    { (short)4, (short)2, "bank_expense", (short)-1, "Расходы банка" },
                    { (short)5, (short)5, "revenue", null, "Выручка" },
                    { (short)6, (short)3, "expense", null, "Расходы" },
                    { (short)7, (short)4, "employee", null, "Операции с сотрудником" }
                });

            migrationBuilder.InsertData(
                table: "categories",
                columns: new[] { "id", "code", "counter_account_id", "counter_category_id", "is_archived", "kind_id", "name" },
                values: new object[,]
                {
                    { 1, "revenue.shift", null, null, false, (short)5, "Выручка смены" },
                    { 2, "revenue.shortage_withheld", null, null, false, (short)5, "Удержание за недостачу" },
                    { 3, "revenue.safe_surplus", null, null, false, (short)5, "Излишек сейфа" },
                    { 4, "expense.payroll", null, null, false, (short)6, "ФОТ" },
                    { 5, "safe.shift_cash", null, null, false, (short)1, "Выручка смены" },
                    { 6, "safe.surplus", null, null, false, (short)1, "Излишек при пересчёте" },
                    { 7, "safe.shortage", null, null, false, (short)2, "Недостача при пересчёте" },
                    { 8, "safe.float_refill", null, null, false, (short)2, "Пополнение размена кассы" },
                    { 9, "bank.acquiring", null, null, false, (short)3, "Эквайринг" },
                    { 10, "employee.accrual", null, null, false, (short)7, "Начисление за смену" },
                    { 11, "employee.shortage", null, null, false, (short)7, "Удержание за недостачу" },
                    { 12, "employee.advance", null, null, false, (short)7, "Аванс" },
                    { 13, "employee.salary", null, null, false, (short)7, "Выплата ЗП" },
                    { 14, "safe.payout", null, null, false, (short)2, "Выплата сотруднику" },
                    { 15, "expense.bank_fee", null, null, false, (short)6, "Комиссия банка" },
                    { 16, "bank.fee", null, null, false, (short)4, "Комиссия банка" },
                    { 17, "bank.incassation", null, null, false, (short)3, "Инкассация" },
                    { 19, "bank.cash_withdrawal", null, null, false, (short)4, "Снятие наличных" },
                    { 21, "expense.household", null, null, false, (short)6, "Хозяйственные" },
                    { 23, "revenue.other", null, null, false, (short)5, "Прочие поступления" },
                    { 25, "employee.fine", null, null, false, (short)7, "Штраф" },
                    { 26, "revenue.fines", null, null, false, (short)5, "Штрафы" },
                    { 27, "employee.bonus", null, null, false, (short)7, "Премия" },
                    { 28, "expense.bonus", null, null, false, (short)6, "Премии" },
                    { 29, "employee.inventory", null, null, false, (short)7, "Инвентаризация" },
                    { 30, "revenue.inventory", null, null, false, (short)5, "Удержание за инвентаризацию" },
                    { 31, "bank.payout", null, null, false, (short)4, "Выплата сотруднику" },
                    { 32, "safe.opening", null, null, false, (short)1, "Стартовый остаток" },
                    { 33, "bank.opening", null, null, false, (short)3, "Стартовый остаток" },
                    { 34, "employee.opening", null, null, false, (short)7, "Стартовый остаток" },
                    { 18, "safe.incassation", 2, 17, false, (short)2, "Инкассация" },
                    { 20, "safe.from_bank", 2, 19, false, (short)1, "Снято с банка" },
                    { 22, "safe.household", 3, 21, false, (short)2, "Хозяйственные" },
                    { 24, "safe.other_income", 4, 23, false, (short)1, "Прочий приход" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_kinds_code",
                table: "account_kinds",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounts_employee_id",
                table: "accounts",
                column: "employee_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_accounts_system_kind",
                table: "accounts",
                column: "kind_id",
                unique: true,
                filter: "employee_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_actor_kinds_code",
                table: "actor_kinds",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bank_receipts_channel_id",
                table: "bank_receipts",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_bank_receipts_confirmed_at",
                table: "bank_receipts",
                column: "confirmed_at");

            migrationBuilder.CreateIndex(
                name: "ix_bank_receipts_fee_transaction_id",
                table: "bank_receipts",
                column: "fee_transaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_bank_receipts_transaction_id",
                table: "bank_receipts",
                column: "transaction_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_categories_code",
                table: "categories",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_categories_counter_account_id",
                table: "categories",
                column: "counter_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_categories_counter_category_id",
                table: "categories",
                column: "counter_category_id");

            migrationBuilder.CreateIndex(
                name: "ix_categories_kind_id_name",
                table: "categories",
                columns: new[] { "kind_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_category_kinds_account_kind_id",
                table: "category_kinds",
                column: "account_kind_id");

            migrationBuilder.CreateIndex(
                name: "ix_category_kinds_code",
                table: "category_kinds",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_token_hash",
                table: "devices",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_employees_telegram_id",
                table: "employees",
                column: "telegram_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_entries_account_id",
                table: "entries",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_entries_category_id",
                table: "entries",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_entries_transaction_id_account_id",
                table: "entries",
                columns: new[] { "transaction_id", "account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_operation_types_code",
                table: "operation_types",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_operations_actor_kind_id",
                table: "operations",
                column: "actor_kind_id");

            migrationBuilder.CreateIndex(
                name: "ix_operations_idempotency_key",
                table: "operations",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_operations_occurred_at",
                table: "operations",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_operations_type_id",
                table: "operations",
                column: "type_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_operation_id",
                table: "outbox_messages",
                column: "operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages",
                column: "sequence",
                filter: "status_id = 1");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_photos_shift_id",
                table: "outbox_messages",
                column: "photos_shift_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_status_id",
                table: "outbox_messages",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_statuses_code",
                table: "outbox_statuses",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_photo_sessions_token",
                table: "photo_sessions",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_photos_session_id",
                table: "photos",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_photos_sha256",
                table: "photos",
                column: "sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_photos_shift_id",
                table: "photos",
                column: "shift_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_channels_code",
                table: "receipt_channels",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_root_users_username",
                table: "root_users",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shift_employees_employee_id",
                table: "shift_employees",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_shifts_closed_at",
                table: "shifts",
                column: "closed_at");

            migrationBuilder.CreateIndex(
                name: "ix_shifts_operation_id",
                table: "shifts",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_operation_id_position",
                table: "transactions",
                columns: new[] { "operation_id", "position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_receipts");

            migrationBuilder.DropTable(
                name: "club_settings");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "entries");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "photos");

            migrationBuilder.DropTable(
                name: "root_users");

            migrationBuilder.DropTable(
                name: "shift_employees");

            migrationBuilder.DropTable(
                name: "receipt_channels");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "transactions");

            migrationBuilder.DropTable(
                name: "outbox_statuses");

            migrationBuilder.DropTable(
                name: "photo_sessions");

            migrationBuilder.DropTable(
                name: "shifts");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "category_kinds");

            migrationBuilder.DropTable(
                name: "operations");

            migrationBuilder.DropTable(
                name: "employees");

            migrationBuilder.DropTable(
                name: "account_kinds");

            migrationBuilder.DropTable(
                name: "actor_kinds");

            migrationBuilder.DropTable(
                name: "operation_types");

            migrationBuilder.DropSequence(
                name: "outbox_messages_sequence");
        }
    }
}
