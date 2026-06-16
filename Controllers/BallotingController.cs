using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using StateLand.Data;
using StateLand.Hubs;
using StateLand.Models.BallotingDTOs.Response;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace StateLand.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BallotingController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly PulsegisContext _dbContext;
        private readonly IWebHostEnvironment _env;
        private readonly IHubContext<BallotingHub> _hubContext;
        public BallotingController(IConfiguration config, PulsegisContext dbContext, IWebHostEnvironment env, IHubContext<BallotingHub> hubContext)
        {
            _config = config;
            _dbContext = dbContext;
            _env = env;
            _hubContext = hubContext;
        }
        public class StoredProcedureRequest
        {
            public string ProcedureName { get; set; } = string.Empty;
            public Dictionary<string, object?>? Parameters { get; set; }
        }
        public class TestStoredProcedureRequest
        {
            public string ProcedureName { get; set; } = string.Empty;
            public Dictionary<string, object?>? Parameters { get; set; }
            public string? RequestId { get; set; }
        }

        [Authorize]
        [HttpPost("RunBalloting")]
        public async Task<IActionResult> RunApplicationBalloting([FromBody] StoredProcedureRequest request)
        {
            using var connection = (SqlConnection)_dbContext.Database.GetDbConnection();
            await connection.OpenAsync();

            using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

            try
            {
                // ================= NORMALIZE =================
                if (request.Parameters != null)
                {
                    foreach (var key in request.Parameters.Keys.ToList())
                    {
                        if (request.Parameters[key] is JsonElement je)
                            request.Parameters[key] = NormalizeValue(je);
                    }
                }

                int? districtId = request.Parameters?.GetValueOrDefault("DistrictId") != null ? Convert.ToInt32(request.Parameters["DistrictId"]) : null;
                int? tehsilId = request.Parameters?.GetValueOrDefault("TehsilId") != null ? Convert.ToInt32(request.Parameters["TehsilId"]) : null;
                int? mauzaId = request.Parameters?.GetValueOrDefault("MauzaId") != null ? Convert.ToInt32(request.Parameters["MauzaId"]) : null;

                districtId = districtId == 0 ? null : districtId;
                tehsilId = tehsilId == 0 ? null : tehsilId;
                mauzaId = mauzaId == 0 ? null : mauzaId;

                // =========================================================
                // 🔴 HIERARCHY BLOCKING (NO DUPLICATE SCOPE RUN)
                // =========================================================
                using (var checkCmd = connection.CreateCommand())
                {
                    checkCmd.Transaction = transaction;

                    checkCmd.CommandText = @"
                SELECT TOP 1 1
                FROM BallotingRuns
                WHERE
                (
                    (@MauzaId IS NOT NULL AND MauzaId = @MauzaId)

                    OR (@MauzaId IS NULL AND @TehsilId IS NOT NULL 
                        AND TehsilId = @TehsilId AND DistrictId = @DistrictId)

                    OR (@TehsilId IS NULL AND @MauzaId IS NULL 
                        AND DistrictId = @DistrictId)
                )
            ";

                    AddParam(checkCmd, "@DistrictId", districtId);
                    AddParam(checkCmd, "@TehsilId", tehsilId);
                    AddParam(checkCmd, "@MauzaId", mauzaId);

                    var exists = await checkCmd.ExecuteScalarAsync();

                    if (exists != null)
                    {
                        return Ok(new { Message = "Balloting already completed for selected scope." });
                    }
                }

                var data = new List<LotteryDataDTO>();

                // ================= FETCH DATA =================
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;

                    cmd.CommandText = @"
               ;WITH ApplicantLotsPriority AS
            (
                SELECT
                    ap.ApplicationId,
                    al.LotUniqueId,
                    ad.DistrictId,
                    ad.TehsilId,
                    ad.MauzaId,
                    ad.FullName,
                    d.district_name,
                    d.district_name_english,
                    t.tehsil_name,
                    t.tehsil_name_english,
                    m.mauza_name,
                    m.mauza_name_english,
                    ad.ApplicantId,
                    ad.CNIC,
                    al.LotId,
                    ROW_NUMBER() OVER
                    (
                        PARTITION BY ap.ApplicantId
                        ORDER BY 
                            CASE 
                                WHEN ad.MauzaId = lb.MauzaId THEN 1 
                                ELSE 2 
                            END
                    ) AS RN

                FROM Application ap
                INNER JOIN ApplicantDetail ad ON ap.ApplicantId = ad.ApplicantId
                INNER JOIN ApplicationLots al ON ap.ApplicationId = al.ApplicationId
                INNER JOIN LotBandi lb ON lb.UniqueId = al.LotUniqueId
                INNER JOIN District d ON ad.DistrictId = d.district_id
                INNER JOIN Tehsil t ON ad.TehsilId = t.tehsil_id
                INNER JOIN Mauza m ON lb.MauzaId = m.mauza_id

                WHERE ap.ApplicationStatus IN ('Approved', 'Appeal Approved')
                  AND (@DistrictId IS NULL OR ad.DistrictId = @DistrictId)
                  AND (@TehsilId IS NULL OR ad.TehsilId = @TehsilId)
                  AND (@MauzaId IS NULL OR lb.MauzaId = @MauzaId)

                  AND NOT EXISTS (
                      SELECT 1
                      FROM BallotingResults br
                      WHERE br.ApplicationId = ap.ApplicationId
                        AND br.LotId = al.LotUniqueId
                  )
            )

            SELECT * FROM ApplicantLotsPriority WHERE RN = 1;
            ";

                    AddParam(cmd, "@DistrictId", districtId);
                    AddParam(cmd, "@TehsilId", tehsilId);
                    AddParam(cmd, "@MauzaId", mauzaId);

                    using var reader = await cmd.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        data.Add(new LotteryDataDTO
                        {
                            ApplicationId = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),

                            LotUniqueId = reader.IsDBNull(1) ? null : reader.GetGuid(1).ToString(),

                            DistrictId = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                            TehsilId = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                            MauzaId = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),

                            ApplicantName = reader.IsDBNull(5) ? "" : reader.GetString(5),

                            DistrictName = reader.IsDBNull(6) ? "" : reader.GetString(6),

                            DistrictNameEnglish = reader.IsDBNull(7) ? "" : reader.GetString(7),

                            TehsilName = reader.IsDBNull(8) ? "" : reader.GetString(8),

                            TehsilNameEnglish = reader.IsDBNull(9) ? "" : reader.GetString(9),

                            MouzaName = reader.IsDBNull(10) ? "" : reader.GetString(10),

                            MouzaNameEnglish = reader.IsDBNull(11) ? "" : reader.GetString(11),

                            ApplicantId = reader.IsDBNull(12) ? 0 : reader.GetInt64(12),
                            LotId = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),

                        });
                    }
                }

                if (data.Count == 0)
                    return Ok(new { Message = "No data found" });

                // ================= LOT VALIDATION =================
                var totalLots = data.Select(x => x.LotUniqueId).Distinct().Count();

                // ================= CREATE RUN =================
                int seed = RandomNumberGenerator.GetInt32(int.MaxValue);
                long runId;

                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;

                    cmd.CommandText = @"
                INSERT INTO BallotingRuns 
                (DistrictId, TehsilId, MauzaId, TotalLots, TotalApplicants, RandomSeed, CreatedAt)
                OUTPUT INSERTED.Id
                VALUES (@DistrictId, @TehsilId, @MauzaId, @Lots, @Apps, @Seed, GETDATE())";

                    AddParam(cmd, "@DistrictId", districtId);
                    AddParam(cmd, "@TehsilId", tehsilId);
                    AddParam(cmd, "@MauzaId", mauzaId);
                    AddParam(cmd, "@Lots", totalLots);
                    AddParam(cmd, "@Apps", data.Select(x => x.ApplicationId).Distinct().Count());
                    AddParam(cmd, "@Seed", seed);

                    runId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                }

                // ================= SHUFFLE =================
                List<T> Shuffle<T>(List<T> list)
                {
                    var rng = new Random(seed);
                    return list.OrderBy(_ => rng.Next()).ToList();
                }

                var finalResults = new List<BallotingResultDTO>();
                var usedApplicants = new HashSet<long>(); // ONLY within this run

                // ================= CORE BALLTING LOGIC =================
                foreach (var lotGroup in data.GroupBy(x => x.LotUniqueId))
                {
                    var first = lotGroup.First();
                    var shuffled = Shuffle(lotGroup.ToList());

                    // ================= WINNER =================
                    foreach (var app in shuffled)
                    {
                        if (!usedApplicants.Contains(app.ApplicantId))
                        {
                            usedApplicants.Add(app.ApplicantId);

                            var winner = new BallotingResultDTO
                            {
                                BallotingRunId = runId,
                                LotId = first.LotUniqueId,
                                ApplicationId = app.ApplicationId,
                                Status = "Winner",
                                Mouza_Id = first.MauzaId,
                                ApplicantName = app.ApplicantName,
                                Total_No_Of_Applications = lotGroup.Count(),
                                DistrictName = first.DistrictName,
                                DistrictNameEnglish = first.DistrictNameEnglish,
                                TehsilName = first.TehsilName,
                                TehsilNameEnglish = first.TehsilNameEnglish,
                                MouzaName = first.MouzaName,
                                MouzaNameEnglish = first.MouzaNameEnglish,
                                ApplicantId = app.ApplicantId,
                                District_Id = app.DistrictId,
                                Tehsil_Id = app.TehsilId,
                                Mauza_Id = app.MauzaId,
                                Lot_Number= app.LotId
                            };

                            finalResults.Add(winner);

                            await Task.Yield();
                            await _hubContext.Clients.All.SendAsync("WinnerSelected", winner);
                            await Task.Delay(120);

                            break;
                        }
                    }

                    // ================= RESERVED =================
                    foreach (var app in shuffled)
                    {
                        if (!usedApplicants.Contains(app.ApplicantId))
                        {
                            usedApplicants.Add(app.ApplicantId);

                            var reserved = new BallotingResultDTO
                            {
                                BallotingRunId = runId,
                                LotId = first.LotUniqueId,
                                ApplicationId = app.ApplicationId,
                                Status = "Reserved",
                                Mouza_Id = first.MauzaId,
                                ApplicantName = app.ApplicantName,
                                Total_No_Of_Applications = lotGroup.Count(),
                                DistrictName = first.DistrictName,
                                DistrictNameEnglish = first.DistrictNameEnglish,
                                TehsilName = first.TehsilName,
                                TehsilNameEnglish = first.TehsilNameEnglish,
                                MouzaName = first.MouzaName,
                                MouzaNameEnglish = first.MouzaNameEnglish,
                                ApplicantId = app.ApplicantId,
                                District_Id = app.DistrictId,
                                Tehsil_Id = app.TehsilId,
                                Mauza_Id = app.MauzaId,
                                Lot_Number = app.LotId
                            };

                            finalResults.Add(reserved);

                            await Task.Yield();
                            await _hubContext.Clients.All.SendAsync("ReservedSelected", reserved);
                            await Task.Delay(120);

                            break;
                        }
                    }
                }

                // ================= SAVE (UNCHANGED - YOUR ORIGINAL LOGIC) =================
                var table = new DataTable();

                table.Columns.Add("BallotingRunId", typeof(long));
                table.Columns.Add("LotId", typeof(string));
                table.Columns.Add("IsActive", typeof(bool));
                table.Columns.Add("CreatedAt", typeof(DateTime));
                table.Columns.Add("IsFinal", typeof(bool));
                table.Columns.Add("Lot_Number", typeof(int));
                table.Columns.Add("Mouza_Id", typeof(int));
                table.Columns.Add("Total_No_Of_Applications", typeof(int));
                table.Columns.Add("ApplicationId", typeof(int));
                table.Columns.Add("Status", typeof(string));
                table.Columns.Add("District_Name", typeof(string));
                table.Columns.Add("District_Name_English", typeof(string));
                table.Columns.Add("Tehsil_Name", typeof(string));
                table.Columns.Add("Tehsil_Name_English", typeof(string));
                table.Columns.Add("Mouza_Name", typeof(string));
                table.Columns.Add("Mouza_Name_English", typeof(string));
                table.Columns.Add("ApplicantId", typeof(int));
                table.Columns.Add("District_Id", typeof(int));
                table.Columns.Add("Tehsil_Id", typeof(int));
                table.Columns.Add("Mauza_Id", typeof(int));

                var now = DateTime.Now;

                foreach (var item in finalResults)
                {
                    table.Rows.Add(
                        item.BallotingRunId,
                        item.LotId,
                        true,
                        now,
                        true,
                        item.Lot_Number,
                        item.Mouza_Id,
                        item.Total_No_Of_Applications,
                        item.ApplicationId,
                        item.Status,
                        item.DistrictName,
                        item.DistrictNameEnglish,
                        item.TehsilName,
                        item.TehsilNameEnglish,
                        item.MouzaName,
                        item.MouzaNameEnglish,
                        item.ApplicantId,
                        item.District_Id,
                        item.Tehsil_Id,
                        item.Mauza_Id
                    );
                }

                using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
                bulk.DestinationTableName = "BallotingResults";

                foreach (DataColumn col in table.Columns)
                    bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

                await bulk.WriteToServerAsync(table);

                await transaction.CommitAsync();

                await _hubContext.Clients.All.SendAsync("BallotingCompleted", new
                {
                    runId,
                    total = finalResults.Count,
                    seed,
                    isCompleted = true
                });

                return Ok(new
                {
                    RunId = runId,
                    Total = finalResults.Count,
                    Seed = seed
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return BadRequest(new { Message = ex.Message });
            }
        }
        private object NormalizeValue(object value)
        {
            if (value is JsonElement je)
            {
                switch (je.ValueKind)
                {
                    case JsonValueKind.Number:
                        if (je.TryGetInt32(out int i)) return i;
                        if (je.TryGetInt64(out long l)) return l;
                        if (je.TryGetDecimal(out decimal d)) return d;
                        break;

                    case JsonValueKind.String:
                        return je.GetString();

                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        return je.GetBoolean();

                    case JsonValueKind.Null:
                        return DBNull.Value;
                }
            }

            return value ?? DBNull.Value;
        }


        private void AddParam(DbCommand cmd, string name, object value)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(param);
        }

        [Authorize]
        [HttpPost("StartBallotingForCDA")]
        public async Task<IActionResult> StartBallotingForCDA([FromBody] StoredProcedureRequest request)
        {
            using var connection = (SqlConnection)_dbContext.Database.GetDbConnection();
            await connection.OpenAsync();

            using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

            try
            {
                if (request.Parameters != null)
                {
                    foreach (var key in request.Parameters.Keys.ToList())
                    {
                        if (request.Parameters[key] is JsonElement je)
                            request.Parameters[key] = NormalizeValue(je);
                    }
                }

                int? districtId = request.Parameters?.GetValueOrDefault("DistrictId") != null ? Convert.ToInt32(request.Parameters["DistrictId"]) : null;
                int? tehsilId = request.Parameters?.GetValueOrDefault("TehsilId") != null ? Convert.ToInt32(request.Parameters["TehsilId"]) : null;
                int? mauzaId = request.Parameters?.GetValueOrDefault("MauzaId") != null ? Convert.ToInt32(request.Parameters["MauzaId"]) : null;

                districtId = districtId == 0 ? null : districtId;
                tehsilId = tehsilId == 0 ? null : tehsilId;
                mauzaId = mauzaId == 0 ? null : mauzaId;

                var data = new List<LotteryDataDTO>();

                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;

                    cmd.CommandText = @"WITH ApplicantLotsPriority
            AS (
                SELECT DISTINCT al.LotUniqueId, al.LotId,
                    m.mauza_id AS MauzaId, m.mauza_name, m.mauza_name_english,
                    t.tehsil_id, t.tehsil_name, t.tehsil_name_english,
                    d.district_id, d.district_name, d.district_name_english,
                    ad.FullName, ad.ApplicantId, ad.CNIC, ap.ApplicationId,
                    CASE WHEN ad.MauzaId=al.MouzaId THEN 1 ELSE 2 END AS RN
                FROM Application_Balloting ap
                INNER JOIN ApplicantDetail_Balloting ad ON ap.ApplicantId = ad.ApplicantId
                INNER JOIN ApplicationLots_Balloting al ON ap.ApplicationId = al.ApplicationId
                INNER JOIN Mauza m ON al.MouzaId = m.mauza_id
                INNER JOIN Tehsil t ON m.tehsil_id = t.tehsil_id
                INNER JOIN District d ON t.district_id = d.district_id
                WHERE (@DistrictId IS NULL OR d.district_id = @DistrictId)
                AND (@TehsilId IS NULL OR t.tehsil_id = @TehsilId)
                AND (@MauzaId IS NULL OR m.mauza_id = @MauzaId)
                AND (t.tehsil_id IN (44,53,57))
                AND NOT EXISTS (
                    SELECT 1 FROM BallotingResults br
                    WHERE br.Mauza_Id = m.mauza_id
                )
            )
            SELECT *
            FROM ApplicantLotsPriority
            ORDER BY tehsil_id, MauzaId, LotUniqueId, RN ASC";

                    AddParam(cmd, "@DistrictId", districtId);
                    AddParam(cmd, "@TehsilId", tehsilId);
                    AddParam(cmd, "@MauzaId", mauzaId);

                    using var reader = await cmd.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        data.Add(new LotteryDataDTO
                        {
                            LotUniqueId = reader.GetGuid(0).ToString(),
                            LotId = Convert.ToInt32(reader[1]),
                            MauzaId = Convert.ToInt32(reader[2]),
                            MouzaName = reader.GetString(3),
                            MouzaNameEnglish = reader.GetString(4),
                            TehsilId = Convert.ToInt32(reader[5]),
                            TehsilName = reader.GetString(6),
                            TehsilNameEnglish = reader.GetString(7),
                            DistrictId = Convert.ToInt32(reader[8]),
                            DistrictName = reader.GetString(9),
                            DistrictNameEnglish = reader.GetString(10),
                            ApplicantName = reader.GetString(11),
                            ApplicantId = reader.GetInt64(12),
                            CNIC = reader.GetString(13),
                            ApplicationId = Convert.ToInt32(reader[14])
                        });
                    }
                }

                if (!data.Any())
                    return BadRequest(new { Message = "No data available" });

                var lotCounts = data.GroupBy(x => x.LotUniqueId)
                    .ToDictionary(g => g.Key, g => g.Count());

                int totalLots = data.Select(x => x.LotUniqueId).Distinct().Count();
                int totalMauzas = data.Select(x => x.MauzaId).Distinct().Count();
                int totalTehsils = data.Select(x => x.TehsilId).Distinct().Count();

                int seed = RandomNumberGenerator.GetInt32(int.MaxValue);
                long runId;

                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;

                    cmd.CommandText = @"INSERT INTO BallotingRuns 
            (DistrictId, TehsilId, MauzaId, TotalLots, TotalApplicants, RandomSeed, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@DistrictId, @TehsilId, @MauzaId, @Lots, @Apps, @Seed, GETDATE())";

                    AddParam(cmd, "@DistrictId", districtId);
                    AddParam(cmd, "@TehsilId", tehsilId);
                    AddParam(cmd, "@MauzaId", mauzaId);
                    AddParam(cmd, "@Lots", totalLots);
                    AddParam(cmd, "@Apps", data.Select(x => x.ApplicationId).Distinct().Count());
                    AddParam(cmd, "@Seed", seed);

                    runId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                }

                var rng = new Random(seed);
                List<T> Shuffle<T>(List<T> list) => list.OrderBy(_ => rng.Next()).ToList();

                var finalResults = new List<BallotingResultDTO>();
                var usedApplicants = new HashSet<long>();

                var lotStatus = data.Select(x => x.LotUniqueId).Distinct()
                    .ToDictionary(x => x, x => false);

                var groups = data
                    .GroupBy(x => new { x.TehsilId, x.MauzaId })
                    .OrderBy(x => x.Key.TehsilId)
                    .ThenBy(x => x.Key.MauzaId);

                await _hubContext.Clients.All.SendAsync("SessionStarted", new
                {
                    RunId = runId,
                    Seed = seed
                });

                foreach (var mouzaGroup in groups)
                {
                    var lots = mouzaGroup.GroupBy(x => x.LotUniqueId);

                    foreach (var lotGroup in lots)
                    {
                        var lotIdStr = lotGroup.Key;

                        var eligibleApplicants = lotGroup
                            .Where(x => !usedApplicants.Contains(x.ApplicantId))
                            .ToList();

                        if (!eligibleApplicants.Any())
                            continue;

                        var shuffled = Shuffle(eligibleApplicants);

                        await _hubContext.Clients.All.SendAsync("LotStarted", new
                        {
                            runId,
                            tehsilId = mouzaGroup.Key.TehsilId,
                            mauzaId = mouzaGroup.Key.MauzaId,
                            lotId = lotIdStr,
                            lotNo = lotGroup.First().LotId,

                            districtName = lotGroup.First().DistrictNameEnglish,
                            tehsilName = lotGroup.First().TehsilNameEnglish,

                            applicants = eligibleApplicants.Select(x => new {
                                applicantId = x.ApplicantId,
                                applicantName = x.ApplicantName,
                                cnic = x.CNIC
                            }),
                            shuffleOrder = shuffled.Select(x => x.ApplicantId)
                        });

                        await Task.Delay(500);

                        await _hubContext.Clients.All.SendAsync("ShuffleStarted", new
                        {
                            RunId = runId,
                            lotId = lotIdStr
                        });

                        await Task.Delay(600);

                        var winner = shuffled.FirstOrDefault();

                        if (winner != null)
                        {
                            usedApplicants.Add(winner.ApplicantId);

                            var res = CreateResult(winner, winner, runId, "Winner", lotCounts[lotIdStr]);
                            finalResults.Add(res);

                            lotStatus[lotIdStr] = true;

                            await _hubContext.Clients.All.SendAsync("WinnerSelected", res);
                        }

                        await Task.Delay(500);

                        var reserved = shuffled
                            .Where(x => winner == null || x.ApplicantId != winner.ApplicantId)
                            .FirstOrDefault();

                        if (reserved != null)
                        {
                            var res = CreateResult(reserved, reserved, runId, "Reserved", lotCounts[lotIdStr]);
                            finalResults.Add(res);

                            await _hubContext.Clients.All.SendAsync("ReservedSelected", res);
                        }

                        await Task.Delay(500);

                        await _hubContext.Clients.All.SendAsync("LotCompleted", new
                        {
                            RunId = runId,
                            LotId = lotIdStr
                        });

                        // ✅ PROGRESS UPDATE
                        var completedLots = lotStatus.Count(x => x.Value);

                        int completedMauzas = finalResults
                            .Where(x => x.Status == "Winner")
                            .Select(x => x.Mouza_Id)
                            .Distinct()
                            .Count();

                        int completedTehsils = finalResults
                            .Where(x => x.Status == "Winner")
                            .GroupBy(x => x.Tehsil_Id)
                            .Count();

                        var percentage = totalLots > 0
                            ? (int)Math.Round((double)completedLots / totalLots * 100)
                            : 0;

                        await _hubContext.Clients.All.SendAsync("ProgressUpdate", new
                        {
                            Lots = new
                            {
                                Completed = completedLots,
                                Total = totalLots,
                                Percentage = percentage
                            },
                            Mauzas = new
                            {
                                Completed = completedMauzas,
                                Total = totalMauzas,
                                Percentage = totalMauzas == 0 ? 0 : (completedMauzas * 100) / totalMauzas
                            },
                            Tehsils = new
                            {
                                Completed = completedTehsils,
                                Total = totalTehsils,
                                Percentage = totalTehsils == 0 ? 0 : (completedTehsils * 100) / totalTehsils
                            }
                        });
                    }
                }

                var table = new DataTable();

                table.Columns.Add("BallotingRunId", typeof(long));
                table.Columns.Add("LotId", typeof(string));
                table.Columns.Add("IsActive", typeof(bool));
                table.Columns.Add("CreatedAt", typeof(DateTime));
                table.Columns.Add("IsFinal", typeof(bool));
                table.Columns.Add("Mouza_Id", typeof(int));
                table.Columns.Add("Total_No_Of_Applications", typeof(int));
                table.Columns.Add("ApplicationId", typeof(int));
                table.Columns.Add("Status", typeof(string));
                table.Columns.Add("District_Name", typeof(string));
                table.Columns.Add("District_Name_English", typeof(string));
                table.Columns.Add("Tehsil_Name", typeof(string));
                table.Columns.Add("Tehsil_Name_English", typeof(string));
                table.Columns.Add("Mouza_Name", typeof(string));
                table.Columns.Add("Mouza_Name_English", typeof(string));
                table.Columns.Add("ApplicantId", typeof(int));
                table.Columns.Add("District_Id", typeof(int));
                table.Columns.Add("Tehsil_Id", typeof(int));
                table.Columns.Add("Mauza_Id", typeof(int));
                table.Columns.Add("Lot_Number", typeof(int));

                foreach (var item in finalResults)
                {
                    table.Rows.Add(
                        item.BallotingRunId,
                        item.LotId,
                        true,
                        DateTime.Now,
                        true,
                        item.Mouza_Id,
                        item.Total_No_Of_Applications,
                        item.ApplicationId,
                        item.Status,
                        item.DistrictName,
                        item.DistrictNameEnglish,
                        item.TehsilName,
                        item.TehsilNameEnglish,
                        item.MouzaName,
                        item.MouzaNameEnglish,
                        item.ApplicantId,
                        item.District_Id,
                        item.Tehsil_Id,
                        item.Mauza_Id,
                        item.Lot_Number
                    );
                }

                using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
                bulk.DestinationTableName = "BallotingResults";

                foreach (DataColumn col in table.Columns)
                    bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

                await bulk.WriteToServerAsync(table);

                await transaction.CommitAsync();

                await _hubContext.Clients.All.SendAsync("BallotingCompleted", new
                {
                    RunId = runId,
                    Seed = seed
                });

                return Ok(new { RunId = runId, Seed = seed });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return BadRequest(new { Message = ex.Message });
            }
        }

        [Authorize]
        [HttpPost("StartBalloting")]
        public async Task<IActionResult> StartBalloting([FromBody] StoredProcedureRequest request)
        {
            using var connection = (SqlConnection)_dbContext.Database.GetDbConnection();
            await connection.OpenAsync();

            using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

            try
            {
                if (request.Parameters != null)
                {
                    foreach (var key in request.Parameters.Keys.ToList())
                    {
                        if (request.Parameters[key] is JsonElement je)
                            request.Parameters[key] = NormalizeValue(je);
                    }
                }

                int? districtId = request.Parameters?.GetValueOrDefault("DistrictId") != null ? Convert.ToInt32(request.Parameters["DistrictId"]) : null;
                int? tehsilId = request.Parameters?.GetValueOrDefault("TehsilId") != null ? Convert.ToInt32(request.Parameters["TehsilId"]) : null;
                int? mauzaId = request.Parameters?.GetValueOrDefault("MauzaId") != null ? Convert.ToInt32(request.Parameters["MauzaId"]) : null;
                string? requestType = request.Parameters?.GetValueOrDefault("RequestType") != null ? Convert.ToString(request.Parameters["RequestType"]) : "";

                districtId = districtId == 0 ? null : districtId;
                tehsilId = tehsilId == 0 ? null : tehsilId;
                mauzaId = mauzaId == 0 ? null : mauzaId;

                var alreadyBalloted = new List<(int? DistrictId, int? TehsilId, int? MouzaId)>();

                using (var checkCmd = connection.CreateCommand())
                {
                    checkCmd.Transaction = transaction;

                    checkCmd.CommandText = @"
                    SELECT DISTINCT District_Id, Tehsil_Id, Mauza_Id
                    FROM BallotingResults
                    WHERE (@DistrictId IS NULL OR District_Id = @DistrictId)
                    AND (@TehsilId IS NULL OR Tehsil_Id = @TehsilId)
                    AND (@MauzaId IS NULL OR Mauza_Id = @MauzaId)";

                    AddParam(checkCmd, "@DistrictId", districtId);
                    AddParam(checkCmd, "@TehsilId", tehsilId);
                    AddParam(checkCmd, "@MauzaId", mauzaId);

                    using var reader = await checkCmd.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        alreadyBalloted.Add((
                            reader.IsDBNull(0) ? null : reader.GetInt32(0),
                            reader.IsDBNull(1) ? null : reader.GetInt32(1),
                            reader.IsDBNull(2) ? null : reader.GetInt32(2)
                        ));
                    }
                }

                // ================= UPDATED CHECKS =================

                // CASE 1 : Specific Mouza Selected
                if (districtId != null && tehsilId != null && mauzaId != null)
                {
                    bool mouzaAlreadyBalloted = alreadyBalloted.Any(x => x.MouzaId == mauzaId);

                    if (mouzaAlreadyBalloted)
                    {
                        return BadRequest(new
                        {
                            Message = "Balloting already done for this mouza"
                        });
                    }
                }
                // ================= END UPDATED CHECKS =================

                var data = new List<LotteryDataDTO>();

                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;

                    cmd.CommandText = @"WITH ApplicantLotsPriority
                AS (
                 SELECT DISTINCT al.LotUniqueId
                    ,al.LotId
                    ,m.mauza_id AS MauzaId
                    ,m.mauza_name
                    ,m.mauza_name_english
                    ,t.tehsil_id
                    ,t.tehsil_name
                    ,t.tehsil_name_english
                    ,d.district_id
                    ,d.district_name
                    ,d.district_name_english
                    ,ad.FullName
                    ,ad.ApplicantId    
                    ,ad.CNIC
                    ,ap.ApplicationId
                    ,CASE WHEN ad.MauzaId=al.MouzaId THEN 1 ELSE 2 END AS RN
                 FROM Application_Balloting ap
                 INNER JOIN ApplicantDetail_Balloting ad ON ap.ApplicantId = ad.ApplicantId
                 INNER JOIN ApplicationLots_Balloting al ON ap.ApplicationId = al.ApplicationId
                 INNER JOIN Mauza m ON al.MouzaId = m.mauza_id
                 INNER JOIN Tehsil t ON m.tehsil_id = t.tehsil_id
                 INNER JOIN District d ON t.district_id = d.district_id
                 WHERE  
                 (
                   @DistrictId IS NULL
                   OR d.district_id = @DistrictId
                   )
                  AND (
                   @TehsilId IS NULL
                   OR t.tehsil_id = @TehsilId
                   )
                  AND (
                   @MauzaId IS NULL
                   OR m.mauza_id = @MauzaId
                   )
                  AND t.tehsil_id<>53 AND t.tehsil_id<>57 AND t.tehsil_id<>44
                  AND NOT EXISTS (
                              SELECT 1
                              FROM BallotingResults br
                              WHERE br.Mauza_Id = m.mauza_id
                          )
                 ) 
                SELECT *
                FROM ApplicantLotsPriority
                ORDER BY tehsil_id,MauzaId,LotUniqueId,RN ASC";
                    

                    AddParam(cmd, "@DistrictId", districtId);
                    AddParam(cmd, "@TehsilId", tehsilId);
                    AddParam(cmd, "@MauzaId", mauzaId);

                    using var reader = await cmd.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        data.Add(new LotteryDataDTO { 
                            LotUniqueId = reader.IsDBNull(0) ? "" : reader.GetGuid(0).ToString(), 
                            LotId = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader[1]), 
                            MauzaId = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader[2]), 
                            MouzaName = reader.IsDBNull(3) ? "" : reader.GetString(3), 
                            MouzaNameEnglish = reader.IsDBNull(4) ? "" : reader.GetString(4), 
                            TehsilId = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader[5]),
                            TehsilName = reader.IsDBNull(6) ? "" : reader.GetString(6), 
                            TehsilNameEnglish = reader.IsDBNull(7) ? "" : reader.GetString(7), 
                            DistrictId = reader.IsDBNull(8) ? 0 : Convert.ToInt32(reader[8]), 
                            DistrictName = reader.IsDBNull(9) ? "" : reader.GetString(9), 
                            DistrictNameEnglish = reader.IsDBNull(10) ? "" : reader.GetString(10), 
                            ApplicantName = reader.IsDBNull(11) ? "" : reader.GetString(11), 
                            ApplicantId = reader.IsDBNull(12) ? 0 : reader.GetInt64(12), 
                            CNIC = reader.IsDBNull(13) ? "" : reader.GetString(13), 
                            ApplicationId = reader.IsDBNull(14) ? 0 : Convert.ToInt32(reader[14]), });
                    }
                }
                if (!data.Any())
                {
                    if (districtId != null && tehsilId == null && mauzaId == null)
                    {
                        return BadRequest(new
                        {
                            Message = "Balloting already done for this district"
                        });
                    }

                    if (districtId != null && tehsilId != null && mauzaId == null)
                    {
                        return BadRequest(new
                        {
                            Message = "Balloting already done for this tehsil"
                        });
                    }

                    if (districtId != null && tehsilId != null && mauzaId != null)
                    {
                        return BadRequest(new
                        {
                            Message = "Balloting already done for this mouza"
                        });
                    }

                    return Ok(new
                    {
                        Message = "No data available"
                    });
                }

                var lotCounts = data.GroupBy(x => x.LotUniqueId).ToDictionary(g => g.Key, g => g.Count());

                int seed = RandomNumberGenerator.GetInt32(int.MaxValue);
                long runId;

                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;

                    cmd.CommandText = @"INSERT INTO BallotingRuns 
    (DistrictId, TehsilId, MauzaId, TotalLots, TotalApplicants, RandomSeed, CreatedAt)
    OUTPUT INSERTED.Id
    VALUES (@DistrictId, @TehsilId, @MauzaId, @Lots, @Apps, @Seed, GETDATE())";

                    AddParam(cmd, "@DistrictId", districtId);
                    AddParam(cmd, "@TehsilId", tehsilId);
                    AddParam(cmd, "@MauzaId", mauzaId);
                    AddParam(cmd, "@Lots", data.Select(x => x.LotUniqueId).Distinct().Count());
                    AddParam(cmd, "@Apps", data.Select(x => x.ApplicationId).Distinct().Count());
                    AddParam(cmd, "@Seed", seed);

                    runId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                }

                int totalLots = data.Select(x => x.LotUniqueId).Distinct().Count();
                int totalMauzas = data.Select(x => x.MauzaId).Distinct().Count();
                int totalTehsils = data.Select(x => x.TehsilId).Distinct().Count();
                int totalDistricts = data.Select(x => x.DistrictId).Distinct().Count();

                await _hubContext.Clients.All.SendAsync("SessionStarted", new
                {
                    RunId = runId,
                    Seed = seed,

                    Totals = new
                    {
                        Lots = totalLots,
                        Mauzas = totalMauzas,
                        Tehsils = totalTehsils,
                        Districts = totalDistricts
                    }
                });

                var rng = new Random(seed);
                List<T> Shuffle<T>(List<T> list) => list.OrderBy(_ => rng.Next()).ToList();

                var finalResults = new List<BallotingResultDTO>();
                var usedApplicants = new HashSet<long>();

                var lotStatus = data.Select(x => x.LotUniqueId).Distinct()
                    .ToDictionary(x => x, x => (Winner: false, Reserved: false));

                var groups = data.GroupBy(x => new { x.TehsilId, x.MauzaId })
                    .OrderBy(x => x.Key.TehsilId)
                    .ThenBy(x => x.Key.MauzaId);

                foreach (var mouzaGroup in groups)
                {
                    var lots = mouzaGroup.GroupBy(x => x.LotUniqueId).OrderBy(x => x.Key);

                    foreach (var lotGroup in lots)
                    {
                        var lotIdStr = lotGroup.Key;
                        var lotApplicants = lotGroup.ToList();

                        var shuffled = Shuffle(lotApplicants);

                        await _hubContext.Clients.All.SendAsync("LotStarted", new
                        {
                            runId = runId,
                            tehsilId = mouzaGroup.Key.TehsilId,
                            mauzaId = mouzaGroup.Key.MauzaId,
                            lotId = lotIdStr,
                            lotNo = lotApplicants.First().LotId,
                            districtName = lotApplicants.First().DistrictNameEnglish,
                            tehsilName = lotApplicants.First().TehsilNameEnglish,
                            applicants = lotApplicants.Select(x => new {
                                applicantId = x.ApplicantId,
                                applicantName = x.ApplicantName,
                                cnic = x.CNIC
                            }),
                            shuffleOrder = shuffled.Select(x => x.ApplicantId)
                        });

                        await Task.Delay(500);

                        await _hubContext.Clients.All.SendAsync("ShuffleStarted", new
                        {
                            RunId = runId,
                            lotId = lotIdStr
                        });

                        await Task.Delay(600);

                        var winner = shuffled.FirstOrDefault(x => !usedApplicants.Contains(x.ApplicantId));

                        if (winner != null)
                        {
                            usedApplicants.Add(winner.ApplicantId);

                            var res = CreateResult(winner, winner, runId, "Winner", lotCounts[lotIdStr]);
                            finalResults.Add(res);

                            lotStatus[lotIdStr] = (true, false);
                            await _hubContext.Clients.All.SendAsync("WinnerSelected", res);
                        }

                        await Task.Delay(500);

                        var reserved = shuffled
                            .Where(x => winner == null || x.ApplicantId != winner.ApplicantId)
                            .FirstOrDefault(x => !usedApplicants.Contains(x.ApplicantId));

                        if (reserved != null)
                        {
                            usedApplicants.Add(reserved.ApplicantId);

                            var res = CreateResult(reserved, reserved, runId, "Reserved", lotCounts[lotIdStr]);
                            finalResults.Add(res);

                            lotStatus[lotIdStr] = (true, true);
                            await _hubContext.Clients.All.SendAsync("ReservedSelected", res);
                        }

                        await Task.Delay(500);

                        await _hubContext.Clients.All.SendAsync("LotCompleted", new
                        {
                            RunId = runId,
                            LotId = lotIdStr
                        });

                        var completedLots = lotStatus.Count(x => x.Value.Winner);

                        int completedMauzas = finalResults
                            .Where(x => x.Status == "Winner")
                            .Select(x => x.Mouza_Id)
                            .Distinct()
                            .Count();

                        int completedTehsils = finalResults
                            .Where(x => x.Status == "Winner")
                            .GroupBy(x => x.Tehsil_Id)
                            .Count();

                        int completedDistricts = finalResults
                            .Where(x => x.Status == "Winner")
                            .GroupBy(x => x.District_Id)
                            .Count();

                        var percentage = totalLots > 0
                            ? (int)Math.Round((double)completedLots / totalLots * 100)
                            : 0;

                        await _hubContext.Clients.All.SendAsync("ProgressUpdate", new
                        {
                            Lots = new
                            {
                                Completed = completedLots,
                                Total = totalLots,
                                Percentage = percentage
                            },
                            Mauzas = new
                            {
                                Completed = completedMauzas,
                                Total = totalMauzas,
                                Percentage = totalMauzas == 0 ? 0 : (completedMauzas * 100) / totalMauzas
                            },
                            Tehsils = new
                            {
                                Completed = completedTehsils,
                                Total = totalTehsils,
                                Percentage = totalTehsils == 0 ? 0 : (completedTehsils * 100) / totalTehsils
                            },

                        });
                    }
                }

                var table = new DataTable();

                table.Columns.Add("BallotingRunId", typeof(long));
                table.Columns.Add("LotId", typeof(string));
                table.Columns.Add("IsActive", typeof(bool));
                table.Columns.Add("CreatedAt", typeof(DateTime));
                table.Columns.Add("IsFinal", typeof(bool));
                table.Columns.Add("Mouza_Id", typeof(int));
                table.Columns.Add("Total_No_Of_Applications", typeof(int));
                table.Columns.Add("ApplicationId", typeof(int));
                table.Columns.Add("Status", typeof(string));
                table.Columns.Add("District_Name", typeof(string));
                table.Columns.Add("District_Name_English", typeof(string));
                table.Columns.Add("Tehsil_Name", typeof(string));
                table.Columns.Add("Tehsil_Name_English", typeof(string));
                table.Columns.Add("Mouza_Name", typeof(string));
                table.Columns.Add("Mouza_Name_English", typeof(string));
                table.Columns.Add("ApplicantId", typeof(int));
                table.Columns.Add("District_Id", typeof(int));
                table.Columns.Add("Tehsil_Id", typeof(int));
                table.Columns.Add("Mauza_Id", typeof(int));
                table.Columns.Add("Lot_Number", typeof(int));

                foreach (var item in finalResults)
                {
                    table.Rows.Add(
                        item.BallotingRunId,
                        item.LotId,
                        true,
                        DateTime.Now,
                        true,
                        item.Mouza_Id,
                        item.Total_No_Of_Applications,
                        item.ApplicationId,
                        item.Status,
                        item.DistrictName,
                        item.DistrictNameEnglish,
                        item.TehsilName,
                        item.TehsilNameEnglish,
                        item.MouzaName,
                        item.MouzaNameEnglish,
                        item.ApplicantId,
                        item.District_Id,
                        item.Tehsil_Id,
                        item.Mauza_Id,
                        item.Lot_Number
                    );
                }

                using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
                bulk.DestinationTableName = "BallotingResults";

                foreach (DataColumn col in table.Columns)
                    bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

                await bulk.WriteToServerAsync(table);

                await transaction.CommitAsync();

                await _hubContext.Clients.All.SendAsync("BallotingCompleted", new
                {
                    RunId = runId,
                    Seed = seed
                });

                return Ok(new { RunId = runId, Seed = seed });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return BadRequest(new { Message = ex.Message });
            }
        }

        //public async Task<IActionResult> StartBalloting([FromBody] StoredProcedureRequest request)
        //{
        //    using var connection = (SqlConnection)_dbContext.Database.GetDbConnection();
        //    await connection.OpenAsync();

        //    using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        //    try
        //    {
        //        if (request.Parameters != null)
        //        {
        //            foreach (var key in request.Parameters.Keys.ToList())
        //            {
        //                if (request.Parameters[key] is JsonElement je)
        //                    request.Parameters[key] = NormalizeValue(je);
        //            }
        //        }

        //        int? districtId = request.Parameters?.GetValueOrDefault("DistrictId") != null ? Convert.ToInt32(request.Parameters["DistrictId"]) : null;
        //        int? tehsilId = request.Parameters?.GetValueOrDefault("TehsilId") != null ? Convert.ToInt32(request.Parameters["TehsilId"]) : null;
        //        int? mauzaId = request.Parameters?.GetValueOrDefault("MauzaId") != null ? Convert.ToInt32(request.Parameters["MauzaId"]) : null;
        //        string? requestType = request.Parameters?.GetValueOrDefault("RequestType") != null ? Convert.ToString(request.Parameters["RequestType"]) : "";

        //        districtId = districtId == 0 ? null : districtId;
        //        tehsilId = tehsilId == 0 ? null : tehsilId;
        //        mauzaId = mauzaId == 0 ? null : mauzaId;


        //        var alreadyBalloted = new List<(int? DistrictId, int? TehsilId, int? MouzaId)>();

        //        using (var checkCmd = connection.CreateCommand())
        //        {
        //            checkCmd.Transaction = transaction;

        //            checkCmd.CommandText = @"
        //            SELECT DISTINCT District_Id, Tehsil_Id, Mauza_Id
        //            FROM BallotingResults
        //            WHERE (@DistrictId IS NULL OR District_Id = @DistrictId)
        //            AND (@TehsilId IS NULL OR Tehsil_Id = @TehsilId)
        //            AND (@MauzaId IS NULL OR Mauza_Id = @MauzaId)
        //            ";

        //            AddParam(checkCmd, "@DistrictId", districtId);
        //            AddParam(checkCmd, "@TehsilId", tehsilId);
        //            AddParam(checkCmd, "@MauzaId", mauzaId);

        //            using var reader = await checkCmd.ExecuteReaderAsync();

        //            while (await reader.ReadAsync())
        //            {
        //                alreadyBalloted.Add((
        //                    reader.IsDBNull(0) ? null : reader.GetInt32(0),
        //                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
        //                    reader.IsDBNull(2) ? null : reader.GetInt32(2)
        //                ));
        //            }
        //        }

        //        if (districtId != null && tehsilId == null && mauzaId == null)
        //        {
        //            if (alreadyBalloted.Any())
        //                return BadRequest(new { Message = "Balloting already done for this district" });
        //        }

        //        if (districtId != null && tehsilId != null && mauzaId == null)
        //        {
        //            if (alreadyBalloted.Any(x => x.TehsilId == tehsilId))
        //                return BadRequest(new { Message = "Balloting already done for this tehsil" });
        //        }

        //        if (districtId != null && tehsilId != null && mauzaId != null)
        //        {
        //            if (alreadyBalloted.Any(x => x.MouzaId == mauzaId))
        //                return BadRequest(new { Message = "Balloting already done for this mouza" });
        //        }

        //        var data = new List<LotteryDataDTO>();

        //        using (var cmd = connection.CreateCommand())
        //        {
        //            cmd.Transaction = transaction;

        //            if(requestType =="CDA")
        //            {
        //                cmd.CommandText = @"WITH ApplicantLotsPriority
        //                AS (
        //                	SELECT ap.ApplicationId
        //                		,al.LotUniqueId
        //                		,ad.DistrictId
        //                		,ad.TehsilId
        //                		,ad.MauzaId
        //                		,ad.FullName
        //                		,d.district_name
        //                		,d.district_name_english
        //                		,t.tehsil_name
        //                		,t.tehsil_name_english
        //                		,m.mauza_name
        //                		,m.mauza_name_english
        //                		,ad.ApplicantId
        //                		,ad.CNIC
        //                		,al.LotId
        //                		,ROW_NUMBER() OVER (
        //                			PARTITION BY ap.ApplicantId ORDER BY CASE 
        //                					WHEN ad.MauzaId = al.MouzaId
        //                						THEN 1
        //                					ELSE 2
        //                					END
        //                			) AS RN
        //                	FROM Application_Balloting ap
        //                	INNER JOIN ApplicantDetail_Balloting ad ON ap.ApplicantId = ad.ApplicantId
        //                	INNER JOIN ApplicationLots_Balloting al ON ap.ApplicationId = al.ApplicationId
        //                	INNER JOIN Mauza m ON al.MouzaId = m.mauza_id
        //                	INNER JOIN Tehsil t ON m.tehsil_id = t.tehsil_id
        //                	INNER JOIN District d ON t.district_id = d.district_id
        //                	WHERE  (
        //                			@DistrictId IS NULL
        //                			OR d.district_id = @DistrictId
        //                			)
        //                		AND (
        //                			@TehsilId IS NULL
        //                			OR t.tehsil_id = @TehsilId
        //                			)
        //                		AND (
        //                			@MauzaId IS NULL
        //                			OR m.mauza_id = @MauzaId
        //                			)
        //                		AND NOT EXISTS (
        //                			SELECT 1
        //                			FROM BallotingResults br
        //                			WHERE  br.LotId = al.LotUniqueId
        //                			)
        //                		AND( t.tehsil_id=53 OR t.tehsil_id=57)
        //                	)
        //                SELECT *
        //                FROM ApplicantLotsPriority
        //                WHERE RN = 1";
        //            }
        //            else
        //            {
        //                cmd.CommandText = @"
        //               WITH ApplicantLotsPriority
        //               AS (
        //               	SELECT ap.ApplicationId
        //               		,al.LotUniqueId
        //               		,ad.DistrictId
        //               		,ad.TehsilId
        //               		,ad.MauzaId
        //               		,ad.FullName
        //               		,d.district_name
        //               		,d.district_name_english
        //               		,t.tehsil_name
        //               		,t.tehsil_name_english
        //               		,m.mauza_name
        //               		,m.mauza_name_english
        //               		,ad.ApplicantId
        //               		,ad.CNIC
        //               		,al.LotId
        //               		,ROW_NUMBER() OVER (
        //               			PARTITION BY ap.ApplicantId ORDER BY CASE 
        //               					WHEN ad.MauzaId = al.MouzaId
        //               						THEN 1
        //               					ELSE 2
        //               					END
        //               			) AS RN
        //               	FROM Application_Balloting ap
        //               	INNER JOIN ApplicantDetail_Balloting ad ON ap.ApplicantId = ad.ApplicantId
        //               	INNER JOIN ApplicationLots_Balloting al ON ap.ApplicationId = al.ApplicationId
        //               	INNER JOIN District d ON ad.DistrictId = d.district_id
        //               	INNER JOIN Tehsil t ON ad.TehsilId = t.tehsil_id
        //               	INNER JOIN Mauza m ON al.MouzaId = m.mauza_id
        //               	WHERE  (
        //               			@DistrictId IS NULL
        //               			OR ad.DistrictId = @DistrictId
        //               			)
        //               		AND (
        //               			@TehsilId IS NULL
        //               			OR ad.TehsilId = @TehsilId
        //               			)
        //               		AND (
        //               			@MauzaId IS NULL
        //               			OR al.MouzaId = @MauzaId
        //               			)
        //               		AND NOT EXISTS (
        //               			SELECT 1
        //               			FROM BallotingResults br
        //               			WHERE  br.LotId = al.LotUniqueId
        //               			)
        //               		AND t.tehsil_id<>53 AND t.tehsil_id<>57
        //               	)
        //               SELECT * FROM ApplicantLotsPriority
        //               WHERE RN = 1";

        //            }


        //            AddParam(cmd, "@DistrictId", districtId);
        //            AddParam(cmd, "@TehsilId", tehsilId);
        //            AddParam(cmd, "@MauzaId", mauzaId);

        //            using var reader = await cmd.ExecuteReaderAsync();

        //            while (await reader.ReadAsync())
        //            {
        //                data.Add(new LotteryDataDTO
        //                {
        //                    ApplicationId = reader.GetInt32(0),
        //                    LotUniqueId = reader.GetGuid(1).ToString(),
        //                    DistrictId = reader.GetInt32(2),
        //                    TehsilId = reader.GetInt32(3),
        //                    MauzaId = reader.GetInt32(4),
        //                    ApplicantName = reader.GetString(5),
        //                    DistrictName = reader.GetString(6),
        //                    DistrictNameEnglish = reader.GetString(7),
        //                    TehsilName = reader.GetString(8),
        //                    TehsilNameEnglish = reader.GetString(9),
        //                    MouzaName = reader.GetString(10),
        //                    MouzaNameEnglish = reader.GetString(11),
        //                    ApplicantId = reader.GetInt64(12),
        //                    CNIC = reader.GetString(13),
        //                    LotId = reader.GetInt32(14)
        //                });
        //            }
        //        }

        //        if (!data.Any())
        //            return Ok(new { Message = "No new data available (already balloted)" });

        //        var lotCounts = data.GroupBy(x => x.LotUniqueId)
        //            .ToDictionary(g => g.Key, g => g.Count());

        //        int seed = RandomNumberGenerator.GetInt32(int.MaxValue);
        //        long runId;

        //        using (var cmd = connection.CreateCommand())
        //        {
        //            cmd.Transaction = transaction;

        //            cmd.CommandText = @"INSERT INTO BallotingRuns 
        //            (DistrictId, TehsilId, MauzaId, TotalLots, TotalApplicants, RandomSeed, CreatedAt)
        //            OUTPUT INSERTED.Id
        //            VALUES (@DistrictId, @TehsilId, @MauzaId, @Lots, @Apps, @Seed, GETDATE())";

        //            AddParam(cmd, "@DistrictId", districtId);
        //            AddParam(cmd, "@TehsilId", tehsilId);
        //            AddParam(cmd, "@MauzaId", mauzaId);
        //            AddParam(cmd, "@Lots", data.Select(x => x.LotUniqueId).Distinct().Count());
        //            AddParam(cmd, "@Apps", data.Select(x => x.ApplicationId).Distinct().Count());
        //            AddParam(cmd, "@Seed", seed);

        //            runId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        //        }


        //        int totalLots = data.Select(x => x.LotUniqueId).Distinct().Count();
        //        int totalMauzas = data.Select(x => x.MauzaId).Distinct().Count();
        //        int totalTehsils = data.Select(x => x.TehsilId).Distinct().Count();
        //        int totalDistricts = data.Select(x => x.DistrictId).Distinct().Count();

        //        await _hubContext.Clients.All.SendAsync("SessionStarted", new
        //        {
        //            RunId = runId,
        //            Seed = seed,

        //            Totals = new
        //            {
        //                Districts = totalDistricts,
        //                Tehsils = totalTehsils,
        //                Mauzas = totalMauzas,
        //                Lots = totalLots
        //            }
        //        });


        //        var rng = new Random(seed);
        //        List<T> Shuffle<T>(List<T> list) => list.OrderBy(_ => rng.Next()).ToList();

        //        var finalResults = new List<BallotingResultDTO>();
        //        var usedApplicants = new HashSet<long>();

        //        var lotStatus = data.Select(x => x.LotUniqueId).Distinct()
        //            .ToDictionary(x => x, x => (Winner: false, Reserved: false));


        //        var groups = data.GroupBy(x => new
        //         {
        //             x.TehsilId,
        //             x.MauzaId
        //         }).OrderBy(x => x.Key.TehsilId).ThenBy(x => x.Key.MauzaId);

        //        foreach (var mouzaGroup in groups)
        //        {
        //            await _hubContext.Clients.All.SendAsync("ShuffleStarted", new { RunId = runId });

        //            var applicants = Shuffle(
        //                mouzaGroup.GroupBy(x => x.ApplicantId)
        //                .Select(g => g.First()).ToList());

        //            foreach (var applicant in applicants)
        //            {
        //                if (usedApplicants.Contains(applicant.ApplicantId))
        //                    continue;

        //                var applicantLots = mouzaGroup
        //                    .Where(x => x.ApplicantId == applicant.ApplicantId).ToList();

        //                bool assigned = false;

        //                // ✅ WINNER
        //                foreach (var lot in Shuffle(applicantLots))
        //                {
        //                    if (!lotStatus[lot.LotUniqueId].Winner)
        //                    {
        //                        usedApplicants.Add(applicant.ApplicantId);

        //                        var res = CreateResult(applicant, lot, runId, "Winner", lotCounts[lot.LotUniqueId]);

        //                        finalResults.Add(res);
        //                        lotStatus[lot.LotUniqueId] = (true, false);

        //                        await _hubContext.Clients.All.SendAsync("WinnerSelected", res);

        //                        int completed = lotStatus.Count(x => x.Value.Winner);


        //                        int completedLots = lotStatus.Count(x => x.Value.Winner);
        //                        int totalLotsCount = lotStatus.Count;


        //                        int completedMauzas = finalResults
        //                            .Where(x => x.Status == "Winner")
        //                            .Select(x => x.Mouza_Id)
        //                            .Distinct()
        //                            .Count();


        //                        int completedTehsils = finalResults
        //                            .Where(x => x.Status == "Winner")
        //                            .GroupBy(x => x.Tehsil_Id)
        //                            .Count(g => g.Any());

        //                        // ✅ SEND FULL DASHBOARD DATA
        //                        await _hubContext.Clients.All.SendAsync("ProgressUpdate", new
        //                        {
        //                            RunId = runId,

        //                            Lots = new
        //                            {
        //                                Completed = completedLots,
        //                                Total = totalLotsCount,
        //                                Percentage = (completedLots * 100) / totalLotsCount
        //                            },

        //                            Mauzas = new
        //                            {
        //                                Completed = completedMauzas,
        //                                Total = totalMauzas,
        //                                Percentage = totalMauzas == 0 ? 0 : (completedMauzas * 100) / totalMauzas
        //                            },

        //                            Tehsils = new
        //                            {
        //                                Completed = completedTehsils,
        //                                Total = totalTehsils,
        //                                Percentage = totalTehsils == 0 ? 0 : (completedTehsils * 100) / totalTehsils
        //                            }
        //                        });


        //                        await Task.Delay(120);
        //                        assigned = true;
        //                        break;
        //                    }
        //                }

        //                if (assigned) continue;

        //                // ✅ RESERVED ✅ (RESTORED)
        //                foreach (var lot in applicantLots)
        //                {
        //                    bool hasWinner = finalResults.Any(x => x.LotId == lot.LotUniqueId && x.Status == "Winner");
        //                    bool hasReserved = finalResults.Any(x => x.LotId == lot.LotUniqueId && x.Status == "Reserved");

        //                    if (hasWinner && !hasReserved)
        //                    {
        //                        usedApplicants.Add(applicant.ApplicantId);

        //                        var res = CreateResult(applicant, lot, runId, "Reserved", lotCounts[lot.LotUniqueId]);

        //                        finalResults.Add(res);
        //                        lotStatus[lot.LotUniqueId] = (true, true);

        //                        await _hubContext.Clients.All.SendAsync("ReservedSelected", res);
        //                        await Task.Delay(120);
        //                        break;
        //                    }
        //                }
        //            }
        //        }

        //        // ✅ ================= BULK INSERT RESTORED ================= ✅

        //        var table = new DataTable();

        //        table.Columns.Add("BallotingRunId", typeof(long));
        //        table.Columns.Add("LotId", typeof(string));
        //        table.Columns.Add("IsActive", typeof(bool));
        //        table.Columns.Add("CreatedAt", typeof(DateTime));
        //        table.Columns.Add("IsFinal", typeof(bool));
        //        table.Columns.Add("Mouza_Id", typeof(int));
        //        table.Columns.Add("Total_No_Of_Applications", typeof(int));
        //        table.Columns.Add("ApplicationId", typeof(int));
        //        table.Columns.Add("Status", typeof(string));
        //        table.Columns.Add("District_Name", typeof(string));
        //        table.Columns.Add("District_Name_English", typeof(string));
        //        table.Columns.Add("Tehsil_Name", typeof(string));
        //        table.Columns.Add("Tehsil_Name_English", typeof(string));
        //        table.Columns.Add("Mouza_Name", typeof(string));
        //        table.Columns.Add("Mouza_Name_English", typeof(string));
        //        table.Columns.Add("ApplicantId", typeof(int));
        //        table.Columns.Add("District_Id", typeof(int));
        //        table.Columns.Add("Tehsil_Id", typeof(int));
        //        table.Columns.Add("Mauza_Id", typeof(int));
        //        table.Columns.Add("Lot_Number", typeof(int));

        //        foreach (var item in finalResults)
        //        {
        //            table.Rows.Add(
        //                item.BallotingRunId,
        //                item.LotId,
        //                true,
        //                DateTime.Now,
        //                true,
        //                item.Mouza_Id,
        //                item.Total_No_Of_Applications,
        //                item.ApplicationId,
        //                item.Status,
        //                item.DistrictName,
        //                item.DistrictNameEnglish,
        //                item.TehsilName,
        //                item.TehsilNameEnglish,
        //                item.MouzaName,
        //                item.MouzaNameEnglish,
        //                item.ApplicantId,
        //                item.District_Id,
        //                item.Tehsil_Id,
        //                item.Mauza_Id,
        //                item.Lot_Number
        //            );
        //        }

        //        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
        //        bulk.DestinationTableName = "BallotingResults";

        //        foreach (DataColumn col in table.Columns)
        //            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

        //        await bulk.WriteToServerAsync(table);

        //        await transaction.CommitAsync();

        //        await _hubContext.Clients.All.SendAsync("BallotingCompleted", new
        //        {
        //            RunId = runId,
        //            Seed = seed
        //        });

        //        return Ok(new { RunId = runId, Seed = seed });
        //    }
        //    catch (Exception ex)
        //    {
        //        await transaction.RollbackAsync();
        //        return BadRequest(new { Message = ex.Message });
        //    }
        //}

        private BallotingResultDTO CreateResult(
        LotteryDataDTO applicant,
        LotteryDataDTO lot,
        long runId,
        string status,
        int totalApplicants)
        {
            return new BallotingResultDTO
            {
                BallotingRunId = runId,
                LotId = lot.LotUniqueId,
                ApplicationId = applicant.ApplicationId,
                ApplicantId = applicant.ApplicantId,
                Status = status,
                District_Id = lot.DistrictId,
                Tehsil_Id = lot.TehsilId,
                Mauza_Id = lot.MauzaId,
                DistrictName = lot.DistrictName,
                DistrictNameEnglish = lot.DistrictNameEnglish,
                TehsilName = lot.TehsilName,
                TehsilNameEnglish = lot.TehsilNameEnglish,
                MouzaName = lot.MouzaName,
                MouzaNameEnglish = lot.MouzaNameEnglish,
                ApplicantName = applicant.ApplicantName,
                CNIC = applicant.CNIC,
                Total_No_Of_Applications = totalApplicants,
                Lot_Number = lot.LotId

            };
        }
       
    }
}
