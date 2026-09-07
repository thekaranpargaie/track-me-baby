using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrackMeBaby.Migrations
{
    /// <inheritdoc />
    public partial class ActivityFreshnessTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_activities_RepositoryFullName",
                table: "activities");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SourceUpdatedAt",
                table: "activities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_activities_RepositoryFullName_Number",
                table: "activities",
                columns: new[] { "RepositoryFullName", "Number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_activities_RepositoryFullName_Number",
                table: "activities");

            migrationBuilder.DropColumn(
                name: "SourceUpdatedAt",
                table: "activities");

            migrationBuilder.CreateIndex(
                name: "IX_activities_RepositoryFullName",
                table: "activities",
                column: "RepositoryFullName");
        }
    }
}
