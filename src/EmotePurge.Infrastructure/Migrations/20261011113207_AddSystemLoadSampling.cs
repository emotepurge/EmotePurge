using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemLoadSampling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SystemLoadHourly",
                columns: table => new
                {
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    HourUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ProcessCpuRateMean = table.Column<double>(type: "double precision", nullable: true),
                    ProcessCpuRateMax = table.Column<double>(type: "double precision", nullable: true),
                    ProcessCpuRateP95 = table.Column<double>(type: "double precision", nullable: true),
                    CgroupCpuRateMean = table.Column<double>(type: "double precision", nullable: true),
                    CgroupCpuRateMax = table.Column<double>(type: "double precision", nullable: true),
                    CgroupCpuRateP95 = table.Column<double>(type: "double precision", nullable: true),
                    HostCpuBusyMean = table.Column<double>(type: "double precision", nullable: true),
                    HostCpuBusyMax = table.Column<double>(type: "double precision", nullable: true),
                    HostCpuBusyP95 = table.Column<double>(type: "double precision", nullable: true),
                    CgroupMemoryCurrentMeanBytes = table.Column<double>(type: "double precision", nullable: true),
                    CgroupMemoryCurrentMaxBytes = table.Column<long>(type: "bigint", nullable: true),
                    CgroupMemoryCurrentP95Bytes = table.Column<long>(type: "bigint", nullable: true),
                    HostMemoryUsedMeanBytes = table.Column<double>(type: "double precision", nullable: true),
                    HostMemoryUsedMaxBytes = table.Column<long>(type: "bigint", nullable: true),
                    HostMemoryUsedP95Bytes = table.Column<long>(type: "bigint", nullable: true),
                    HostLoadAverage1Mean = table.Column<double>(type: "double precision", nullable: true),
                    HostLoadAverage1Max = table.Column<double>(type: "double precision", nullable: true),
                    HostLoadAverage1P95 = table.Column<double>(type: "double precision", nullable: true),
                    LastCpuQuotaMicroseconds = table.Column<long>(type: "bigint", nullable: true),
                    LastCpuPeriodMicroseconds = table.Column<long>(type: "bigint", nullable: true),
                    LastCgroupMemoryMaxBytes = table.Column<long>(type: "bigint", nullable: true),
                    LastHostMemoryTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    ChatLines = table.Column<long>(type: "bigint", nullable: true),
                    EmoteMatches = table.Column<long>(type: "bigint", nullable: true),
                    ActiveChannelsMean = table.Column<double>(type: "double precision", nullable: true),
                    ActiveChannelsMax = table.Column<int>(type: "integer", nullable: true),
                    LiveChannelsMean = table.Column<double>(type: "double precision", nullable: true),
                    LiveChannelsMax = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemLoadHourly", x => new { x.Source, x.HourUtc });
                });

            migrationBuilder.CreateTable(
                name: "SystemLoadSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SampledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProcessInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousSampledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProcessCpuTimeTicks = table.Column<long>(type: "bigint", nullable: true),
                    CgroupCpuUsageMicroseconds = table.Column<long>(type: "bigint", nullable: true),
                    CgroupCpuQuotaMicroseconds = table.Column<long>(type: "bigint", nullable: true),
                    CgroupCpuPeriodMicroseconds = table.Column<long>(type: "bigint", nullable: true),
                    CgroupMemoryCurrentBytes = table.Column<long>(type: "bigint", nullable: true),
                    CgroupMemoryMaxBytes = table.Column<long>(type: "bigint", nullable: true),
                    HostCpuBusyJiffies = table.Column<long>(type: "bigint", nullable: true),
                    HostCpuTotalJiffies = table.Column<long>(type: "bigint", nullable: true),
                    HostCpuIowaitJiffies = table.Column<long>(type: "bigint", nullable: true),
                    HostCpuStealJiffies = table.Column<long>(type: "bigint", nullable: true),
                    HostLoadAverage1 = table.Column<double>(type: "double precision", nullable: true),
                    HostLoadAverage5 = table.Column<double>(type: "double precision", nullable: true),
                    HostLoadAverage15 = table.Column<double>(type: "double precision", nullable: true),
                    HostMemoryTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    HostMemoryAvailableBytes = table.Column<long>(type: "bigint", nullable: true),
                    HostSwapTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    HostSwapFreeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ActiveChannels = table.Column<int>(type: "integer", nullable: true),
                    LiveChannels = table.Column<int>(type: "integer", nullable: true),
                    ChatLines = table.Column<long>(type: "bigint", nullable: true),
                    EmoteMatches = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemLoadSamples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SystemLoadSamples_Source_ProcessInstanceId_SampledAtUtc",
                table: "SystemLoadSamples",
                columns: new[] { "Source", "ProcessInstanceId", "SampledAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemLoadSamples_Source_SampledAtUtc",
                table: "SystemLoadSamples",
                columns: new[] { "Source", "SampledAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SystemLoadHourly");

            migrationBuilder.DropTable(
                name: "SystemLoadSamples");
        }
    }
}
