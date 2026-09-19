using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccessLab.Api.Migrations
{
    /// <inheritdoc />
    public partial class AcceptedDecisionContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcceptedPreviewId",
                table: "Requests",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "DecisionContextJson",
                table: "Requests",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "DecisionContextJson",
                table: "Previews",
                type: "text",
                nullable: true
            );
            migrationBuilder.Sql(
                """
                CREATE FUNCTION protect_decision_context() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."AcceptedPreviewId" IS NOT NULL AND
                        (NEW."AcceptedPreviewId" IS DISTINCT FROM OLD."AcceptedPreviewId" OR
                         NEW."DecisionContextJson" IS DISTINCT FROM OLD."DecisionContextJson") THEN
                        RAISE EXCEPTION 'Accepted decision context is immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER immutable_decision_context BEFORE UPDATE ON "Requests"
                    FOR EACH ROW EXECUTE FUNCTION protect_decision_context();
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """DROP TRIGGER immutable_decision_context ON "Requests"; DROP FUNCTION protect_decision_context();"""
            );
            migrationBuilder.DropColumn(name: "AcceptedPreviewId", table: "Requests");

            migrationBuilder.DropColumn(name: "DecisionContextJson", table: "Requests");

            migrationBuilder.DropColumn(name: "DecisionContextJson", table: "Previews");
        }
    }
}
