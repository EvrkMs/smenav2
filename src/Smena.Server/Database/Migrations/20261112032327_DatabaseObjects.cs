using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Smena.Server.Database.Migrations
{
    /// <summary>
    /// Объекты базы, которые EF Core не описывает моделью: проверка баланса
    /// транзакции и представления для чтения движений по счетам и остатка банка.
    /// </summary>
    public partial class DatabaseObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Проверка одной транзакции. Правила те же, что в TransactionDraft:
            // ровно две проводки; изменение по стороне «где деньги» (side = 1)
            // равно изменению по стороне «чьи деньги» (side = 2).
            // Если транзакции уже нет (её удалили вместе с проводками) — проверять нечего.
            migrationBuilder.Sql("""
                CREATE FUNCTION ledger_assert_transaction(tx uuid) RETURNS void
                LANGUAGE plpgsql AS $$
                DECLARE
                    entry_count int;
                    asset_change int;
                    source_change int;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM transactions WHERE id = tx) THEN
                        RETURN;
                    END IF;

                    SELECT count(*),
                           coalesce(sum(e.direction) FILTER (WHERE k.side = 1), 0),
                           coalesce(sum(e.direction) FILTER (WHERE k.side = 2), 0)
                      INTO entry_count, asset_change, source_change
                      FROM entries e
                      JOIN accounts a ON a.id = e.account_id
                      JOIN account_kinds k ON k.id = a.kind_id
                     WHERE e.transaction_id = tx;

                    IF entry_count <> 2 THEN
                        RAISE EXCEPTION 'Транзакция % должна иметь ровно две проводки, найдено %', tx, entry_count
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'ledger_transaction_two_entries';
                    END IF;

                    IF asset_change <> source_change THEN
                        RAISE EXCEPTION 'Транзакция % не сбалансирована', tx
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'ledger_transaction_balanced';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION ledger_entries_check() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        PERFORM ledger_assert_transaction(OLD.transaction_id);
                    END IF;
                    IF TG_OP <> 'DELETE' THEN
                        PERFORM ledger_assert_transaction(NEW.transaction_id);
                    END IF;
                    RETURN NULL;
                END
                $$;

                CREATE FUNCTION ledger_transactions_check() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM ledger_assert_transaction(NEW.id);
                    RETURN NULL;
                END
                $$;
                """);

            // Отложенные триггеры: проверка идёт в момент фиксации, когда обе проводки
            // уже записаны. Срабатывают и на правки руками — удалить или добавить одну
            // проводку отдельно база не даст, а удалить транзакцию целиком можно.
            migrationBuilder.Sql("""
                CREATE CONSTRAINT TRIGGER entries_ledger_check
                    AFTER INSERT OR UPDATE OR DELETE ON entries
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION ledger_entries_check();

                CREATE CONSTRAINT TRIGGER transactions_ledger_check
                    AFTER INSERT ON transactions
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION ledger_transactions_check();
                """);

            migrationBuilder.Sql("""
                CREATE VIEW account_movements AS
                SELECT e.id                          AS entry_id,
                       e.account_id,
                       a.kind_id                     AS account_kind,
                       e.direction,
                       t.amount,
                       t.amount * e.direction        AS signed_amount,
                       e.category_id,
                       t.id                          AS transaction_id,
                       t.note,
                       o.id                          AS operation_id,
                       o.type_id                     AS operation_type,
                       o.occurred_at,
                       o.comment,
                       (o.cancelled_at IS NOT NULL)  AS is_cancelled
                  FROM entries e
                  JOIN transactions t ON t.id = e.transaction_id
                  JOIN operations o ON o.id = t.operation_id
                  JOIN accounts a ON a.id = e.account_id;
                """);

            // Остаток банка двумя числами — для ручных запросов. Сервер считает
            // те же числа в BankReceiptService.GetBalanceAsync.
            // total      — баланс счёта «Банк» (id = 2) по проводкам;
            // in_transit — отправлено в банк, но ещё не подтверждено;
            // confirmed  — то, что должно совпадать с выпиской.
            migrationBuilder.Sql("""
                CREATE VIEW bank_balance AS
                SELECT b.total,
                       p.in_transit,
                       b.total - p.in_transit AS confirmed
                  FROM (SELECT coalesce(sum(signed_amount), 0)::int AS total
                          FROM account_movements
                         WHERE account_id = 2 AND NOT is_cancelled) b,
                       (SELECT coalesce(sum(t.amount), 0)::int AS in_transit
                          FROM bank_receipts r
                          JOIN transactions t ON t.id = r.transaction_id
                          JOIN operations o ON o.id = t.operation_id
                         WHERE r.confirmed_at IS NULL AND o.cancelled_at IS NULL) p;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP VIEW bank_balance;
                DROP VIEW account_movements;
                DROP TRIGGER transactions_ledger_check ON transactions;
                DROP TRIGGER entries_ledger_check ON entries;
                DROP FUNCTION ledger_transactions_check();
                DROP FUNCTION ledger_entries_check();
                DROP FUNCTION ledger_assert_transaction(uuid);
                """);
        }
    }
}
