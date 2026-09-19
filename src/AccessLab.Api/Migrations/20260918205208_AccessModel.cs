using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccessLab.Api.Migrations
{
    /// <inheritdoc />
    public partial class AccessModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ProjectId = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Requestable = table.Column<bool>(type: "boolean", nullable: false),
                    ResourceIds = table.Column<string[]>(type: "text[]", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    AccountId = table.Column<string>(type: "text", nullable: false),
                    GroupId = table.Column<string>(type: "text", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    EndsAt = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RequestId = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "Resources",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ProjectId = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    RuleVersion = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "SyncStates",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "text", nullable: false),
                    DesiredRevision = table.Column<long>(type: "bigint", nullable: false),
                    ObservedRevision = table.Column<long>(type: "bigint", nullable: false),
                    Desired = table.Column<string[]>(type: "text[]", nullable: false),
                    Observed = table.Column<string[]>(type: "text[]", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncStates", x => x.AccountId);
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_AccountId_GroupId",
                table: "Memberships",
                columns: new[] { "AccountId", "GroupId" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Groups");

            migrationBuilder.DropTable(name: "Memberships");

            migrationBuilder.DropTable(name: "Resources");

            migrationBuilder.DropTable(name: "SyncStates");
        }
    }
}
