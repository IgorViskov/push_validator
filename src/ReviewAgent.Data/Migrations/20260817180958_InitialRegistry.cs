using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReviewAgent.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SolutionPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Token = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    HookInstalled = table.Column<bool>(type: "INTEGER", nullable: false),
                    HookInstalledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastIndexedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    GraphNodes = table.Column<long>(type: "INTEGER", nullable: false),
                    GraphEdges = table.Column<long>(type: "INTEGER", nullable: false),
                    LastIndexError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Complexity = table.Column<int>(type: "INTEGER", nullable: false),
                    ExecutionModel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CostUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    ModelCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    GraphQueries = table.Column<int>(type: "INTEGER", nullable: false),
                    ImpactChunks = table.Column<int>(type: "INTEGER", nullable: false),
                    DiffLines = table.Column<int>(type: "INTEGER", nullable: false),
                    AbortReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    GraphStatus = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FindingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CriticalCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RouteJson = table.Column<string>(type: "TEXT", nullable: false),
                    GuardrailsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ModelCallsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ToolCallsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DegradationsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Runs_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Findings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    File = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Findings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Findings_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Findings_RunId",
                table: "Findings",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_Key",
                table: "Repositories",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_Path",
                table: "Repositories",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Runs_RepositoryId_StartedAt",
                table: "Runs",
                columns: new[] { "RepositoryId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Findings");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "Repositories");
        }
    }
}
