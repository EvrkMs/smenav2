using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Smena.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class OutboxReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "reply_to_sequence",
                table: "outbox_messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_reply_to_sequence",
                table: "outbox_messages",
                column: "reply_to_sequence");

            migrationBuilder.AddCheckConstraint(
                name: "ck_outbox_messages_reply_is_text",
                table: "outbox_messages",
                sql: "reply_to_sequence IS NULL OR photos_shift_id IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_outbox_messages_outbox_messages_reply_to_sequence",
                table: "outbox_messages",
                column: "reply_to_sequence",
                principalTable: "outbox_messages",
                principalColumn: "sequence",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_outbox_messages_outbox_messages_reply_to_sequence",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_reply_to_sequence",
                table: "outbox_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_outbox_messages_reply_is_text",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "reply_to_sequence",
                table: "outbox_messages");
        }
    }
}
