using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace GazetteAI.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApplyEmbeddingDimension768 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Vector>(
                name: "Embedding",
                table: "DocumentChunks",
                type: "vector(768)",
                nullable: false,
                oldClrType: typeof(Vector),
                oldType: "vector(1536)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Vector>(
                name: "Embedding",
                table: "DocumentChunks",
                type: "vector(1536)",
                nullable: false,
                oldClrType: typeof(Vector),
                oldType: "vector(768)");
        }
    }
}
