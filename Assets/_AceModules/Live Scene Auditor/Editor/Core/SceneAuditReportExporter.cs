using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace AceModules.LiveSceneAuditor
{
    public static class SceneAuditReportExporter
    {
        public static string GenerateDefaultFileName()
        {
            string productName = SanitizeFileName(Application.productName);
            if (string.IsNullOrWhiteSpace(productName)) productName = "Product";

            string sceneName = SanitizeFileName(SceneManager.GetActiveScene().name);
            if (string.IsNullOrWhiteSpace(sceneName)) sceneName = "UntitledScene";

            string dateTime = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

            return $"report_{productName}_{sceneName}_{dateTime}.pdf";
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Unnamed";
            char[] invalidChars = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (Array.IndexOf(invalidChars, c) < 0 && c != ' ' && c != '#')
                {
                    sb.Append(c);
                }
                else if (c == ' ')
                {
                    sb.Append('_');
                }
            }
            return sb.ToString().Trim('_');
        }

        public static bool ExportPdf(SceneAuditReport report, BudgetProfile profile, bool includeInactive, string targetPdfPath)
        {
            if (report == null)
            {
                Debug.LogError("[Live Scene Auditor] Cannot export PDF: Report data is null.");
                return false;
            }

            string html = BuildHtmlReport(report, profile, includeInactive);
            string browserPath = FindBrowserExecutablePath();

            if (!string.IsNullOrEmpty(browserPath))
            {
                bool success = TryConvertHtmlToPdfWithBrowser(html, browserPath, targetPdfPath);
                if (success && File.Exists(targetPdfPath) && new FileInfo(targetPdfPath).Length > 0)
                {
                    return true;
                }
                Debug.LogWarning("[Live Scene Auditor] Browser PDF conversion failed. Falling back to built-in pure C# PDF generator.");
            }
            else
            {
                Debug.LogWarning("[Live Scene Auditor] Headless browser not found. Falling back to built-in pure C# PDF generator.");
            }

            // Fallback: Pure C# Vector PDF Generator (Zero external dependencies)
            return GenerateFallbackPdf(report, profile, includeInactive, targetPdfPath);
        }

        private static bool TryConvertHtmlToPdfWithBrowser(string html, string browserPath, string targetPdfPath)
        {
            string tempHtmlPath = Path.Combine(Path.GetTempPath(), $"audit_report_{Guid.NewGuid():N}.html");
            try
            {
                File.WriteAllText(tempHtmlPath, html, Encoding.UTF8);

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = browserPath,
                    Arguments = $"--headless --disable-gpu --no-pdf-header-footer --print-to-pdf=\"{targetPdfPath}\" \"{tempHtmlPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using Process proc = Process.Start(psi);
                if (proc == null) return false;

                bool exited = proc.WaitForExit(15000); // 15s timeout
                if (!exited)
                {
                    try { proc.Kill(); } catch {}
                    return false;
                }

                return proc.ExitCode == 0 && File.Exists(targetPdfPath) && new FileInfo(targetPdfPath).Length > 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Live Scene Auditor] Headless browser conversion exception: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempHtmlPath)) File.Delete(tempHtmlPath);
                }
                catch {}
            }
        }

        private static string FindBrowserExecutablePath()
        {
            // 1. Standard Microsoft Edge locations (Pre-installed on every Windows 10/11 system)
            string edgeX86 = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            if (File.Exists(edgeX86)) return edgeX86;

            string edge64 = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
            if (File.Exists(edge64)) return edge64;

            // 2. Google Chrome locations
            string chrome64 = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
            if (File.Exists(chrome64)) return chrome64;

            string chromeX86 = @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe";
            if (File.Exists(chromeX86)) return chromeX86;

            // 3. User Local AppData installations
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                string localEdge = Path.Combine(localAppData, @"Microsoft\Edge\Application\msedge.exe");
                if (File.Exists(localEdge)) return localEdge;

                string localChrome = Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe");
                if (File.Exists(localChrome)) return localChrome;
            }

            // 4. Windows Registry App Paths
            try
            {
                object regEdge = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", "", null);
                if (regEdge is string pathEdge && File.Exists(pathEdge)) return pathEdge;

                object regChrome = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", "", null);
                if (regChrome is string pathChrome && File.Exists(pathChrome)) return pathChrome;
            }
            catch {}

            return null;
        }

        public static string BuildHtmlReport(SceneAuditReport report, BudgetProfile profile, bool includeInactive)
        {
            if (report == null) return "<html><body><h1>No Report Data</h1></body></html>";
            if (profile == null) profile = BudgetProfile.GetDefault();

            StringBuilder sb = new StringBuilder(32768);

            string healthStatusClass = report.isOverBudget ? "badge-fail" : "badge-pass";
            string healthStatusText = report.isOverBudget 
                ? $"FAIL - OVER BUDGET ({report.overallHealthPercentage:F0}%)" 
                : $"PASS - WITHIN BUDGET ({report.overallHealthPercentage:F0}%)";

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang='en'>");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset='utf-8'>");
            sb.AppendLine("<title>Scene Performance Audit Report</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(@"
                @page {
                    size: A4 portrait;
                    margin: 12mm 10mm;
                }
                * { box-sizing: border-box; }
                body {
                    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
                    color: #1f2937;
                    background-color: #ffffff;
                    margin: 0;
                    padding: 0;
                    font-size: 10px;
                    line-height: 1.35;
                }
                .header-banner {
                    border-bottom: 2px solid #2563eb;
                    padding-bottom: 8px;
                    margin-bottom: 12px;
                    display: flex;
                    justify-content: space-between;
                    align-items: flex-start;
                }
                .header-title h1 {
                    margin: 0 0 2px 0;
                    font-size: 18px;
                    color: #1e3a8a;
                    font-weight: 700;
                }
                .header-title .subtitle {
                    color: #4b5563;
                    font-size: 10.5px;
                }
                .header-meta {
                    text-align: right;
                    font-size: 9.5px;
                    color: #6b7280;
                }
                .status-card {
                    padding: 8px 12px;
                    border-radius: 6px;
                    margin-bottom: 14px;
                    display: flex;
                    justify-content: space-between;
                    align-items: center;
                }
                .status-pass {
                    background-color: #ecfdf5;
                    border: 1px solid #a7f3d0;
                    color: #065f46;
                }
                .status-fail {
                    background-color: #fef2f2;
                    border: 1px solid #fecaca;
                    color: #991b1b;
                }
                h2 {
                    font-size: 12.5px;
                    color: #1e40af;
                    border-bottom: 1.5px solid #dbeafe;
                    padding-bottom: 3px;
                    margin: 14px 0 6px 0;
                    page-break-after: avoid;
                    font-weight: 700;
                }
                h3 {
                    font-size: 11px;
                    color: #1f2937;
                    margin: 10px 0 4px 0;
                    page-break-after: avoid;
                    font-weight: 600;
                }
                table {
                    width: 100%;
                    border-collapse: collapse;
                    margin-bottom: 10px;
                    font-size: 9.5px;
                    page-break-inside: auto;
                }
                tr {
                    page-break-inside: avoid;
                    page-break-after: auto;
                }
                th {
                    background-color: #1e40af;
                    color: #ffffff;
                    text-align: left;
                    padding: 5px 6px;
                    font-weight: 600;
                    border: 1px solid #1e40af;
                    font-size: 9.5px;
                }
                td {
                    padding: 4.5px 6px;
                    border: 1px solid #e5e7eb;
                    vertical-align: top;
                }
                tr:nth-child(even) td {
                    background-color: #f9fafb;
                }
                .num {
                    text-align: right;
                    font-variant-numeric: tabular-nums;
                }
                .badge {
                    display: inline-block;
                    padding: 1.5px 5px;
                    border-radius: 3px;
                    font-size: 8.5px;
                    font-weight: 600;
                    white-space: nowrap;
                }
                .badge-pass { background-color: #d1fae5; color: #065f46; }
                .badge-warn { background-color: #fef3c7; color: #92400e; }
                .badge-fail { background-color: #fee2e2; color: #991b1b; }
                .badge-mesh { background-color: #e0e7ff; color: #3730a3; }
                .badge-tex  { background-color: #ede9fe; color: #5b21b6; }
                .badge-light{ background-color: #fef9c3; color: #854d0e; }
                .alert-pill {
                    display: inline-block;
                    background-color: #fee2e2;
                    color: #991b1b;
                    padding: 1px 4px;
                    border-radius: 3px;
                    font-size: 8px;
                    font-weight: 600;
                    margin: 1px 2px 1px 0;
                }
                .subtle { color: #6b7280; font-size: 8.5px; }
                .footer {
                    margin-top: 14px;
                    border-top: 1px solid #e5e7eb;
                    padding-top: 6px;
                    text-align: center;
                    color: #9ca3af;
                    font-size: 8.5px;
                }
                .card {
                    background-color: #f9fafb;
                    border: 1px solid #e5e7eb;
                    border-radius: 5px;
                    padding: 8px;
                    margin-bottom: 8px;
                    page-break-inside: avoid;
                }
            ");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            // Header Banner
            sb.AppendLine("<div class='header-banner'>");
            sb.AppendLine("  <div class='header-title'>");
            sb.AppendLine("    <h1>Live Scene Auditor - Performance Report</h1>");
            sb.AppendLine($"    <div class='subtitle'><b>Project:</b> {Escape(Application.productName)} &nbsp;|&nbsp; <b>Scene:</b> {Escape(SceneManager.GetActiveScene().name)} &nbsp;|&nbsp; <b>Scope:</b> Entire Scene (All Objects)</div>");
            sb.AppendLine("  </div>");
            sb.AppendLine("  <div class='header-meta'>");
            sb.AppendLine($"    <div><b>Date:</b> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");
            sb.AppendLine($"    <div><b>Unity:</b> {Escape(Application.unityVersion)} &nbsp;|&nbsp; <b>Platform:</b> {EditorUserBuildSettings.activeBuildTarget}</div>");
            sb.AppendLine($"    <div><b>Execution Time:</b> {report.scanDurationMs:F1} ms</div>");
            sb.AppendLine("  </div>");
            sb.AppendLine("</div>");

            // Overall Health Status Bar
            string cardClass = report.isOverBudget ? "status-fail" : "status-pass";
            sb.AppendLine($"<div class='status-card {cardClass}'>");
            sb.AppendLine($"  <div><b>Overall Budget Status:</b> <span class='badge {healthStatusClass}'>{healthStatusText}</span></div>");
            sb.AppendLine($"  <div><b>Health Score:</b> <b>{report.overallHealthPercentage:F0}%</b> (100% = At Budget Limit)</div>");
            sb.AppendLine("</div>");

            // SECTION 1: SETTINGS & BUDGET PROFILE (AT TOP)
            sb.AppendLine("<h2>1. Settings & Budget Profile</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("  <tr><th>Target Performance Budgets</th><th>Limit</th><th>Lighting Budgets</th><th>Limit</th><th>Single-Asset Alert Rules</th><th>Threshold</th></tr>");
            sb.AppendLine($"  <tr><td>Max Scene Triangles</td><td class='num'><b>{profile.maxTriangles:N0}</b> tris</td><td>Max Directional Lights</td><td class='num'><b>{profile.maxDirectionalLights}</b></td><td>Heavy Mesh Alert</td><td class='num'>&ge; {profile.singleMeshHeavyTriangles:N0} tris</td></tr>");
            sb.AppendLine($"  <tr><td>Max Scene Vertices</td><td class='num'><b>{profile.maxVertices:N0}</b> verts</td><td>Max Point Lights</td><td class='num'><b>{profile.maxPointLights}</b></td><td>Multi-Material Alert</td><td class='num'>&gt; {profile.maxMaterialsPerObject} mats</td></tr>");
            sb.AppendLine($"  <tr><td>Max Scene Materials</td><td class='num'><b>{profile.maxMaterialsInScene}</b> mats</td><td>Max Spot Lights</td><td class='num'><b>{profile.maxSpotLights}</b></td><td>Missing LOD Alert</td><td class='num'>&ge; {profile.requireLodAboveTriangles:N0} tris</td></tr>");
            sb.AppendLine($"  <tr><td>Max Scene FBX Models</td><td class='num'><b>{profile.maxFbxInScene}</b> models</td><td>Max Total Lights</td><td class='num'><b>{profile.maxTotalLights}</b></td><td>Inactive Objects Scan</td><td class='num'>{(includeInactive ? "Included" : "Ignored")}</td></tr>");
            sb.AppendLine($"  <tr><td>Max Texture Area Score</td><td class='num'><b>{profile.maxTextureScore:F1}</b> pts</td><td>Max Realtime Shadow Casters</td><td class='num'><b>{profile.maxRealtimeShadowCasters}</b></td><td>Scan Scope</td><td class='num'>Entire Scene</td></tr>");
            sb.AppendLine("</table>");

            // SECTION 2: OVERVIEW & BUDGET STATUS
            sb.AppendLine("<h2>2. Overview & Budget Status</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("  <tr><th>Metric</th><th class='num'>Scene Value</th><th class='num'>Budget Limit</th><th class='num'>Usage %</th><th style='text-align:center;'>Status</th><th>Notes</th></tr>");

            AppendMetricRow(sb, "Total Scene Triangles", report.totalSceneTriangles, profile.maxTriangles, "tris", "Full geometry polygon count in scene");
            AppendMetricRow(sb, "Total Scene Vertices", report.totalSceneVertices, profile.maxVertices, "verts", "Vertex footprint across all scene meshes");
            AppendMetricRow(sb, "Unique Scene Materials", report.uniqueMaterialsCount, profile.maxMaterialsInScene, "materials", "Distinct material assets causing draw call switches");
            AppendMetricRow(sb, "FBX Models in Scene", report.fbxModelCount, profile.maxFbxInScene, "models", "Unique 3D model asset files");
            AppendMetricFloatRow(sb, "Texture Area Score", report.textureAreaScore, profile.maxTextureScore, "pts", "Accumulated VRAM footprint (512=1pt, 1024=4pts, 2048=16pts)");
            AppendMetricRow(sb, "Total Scene Lights", report.totalLightsCount, profile.maxTotalLights, "lights", $"Active: {report.totalLightsCount}, Inactive: {report.inactiveLightsCount}");
            AppendMetricRow(sb, "Directional Lights", report.directionalLightsCount, profile.maxDirectionalLights, "lights", "Infinite reach lights (always computed)");
            AppendMetricRow(sb, "Point Lights", report.pointLightsCount, profile.maxPointLights, "lights", "Spherical range illumination");
            AppendMetricRow(sb, "Spot Lights", report.spotLightsCount, profile.maxSpotLights, "lights", "Conical range illumination");
            AppendMetricRow(sb, "Realtime Shadow Casters", report.shadowCasterLightsCount, profile.maxRealtimeShadowCasters, "casters", "Lights rendering dynamic depth/cubemap shadow passes");

            sb.AppendLine("</table>");

            // Lighting Breakdown Strip
            sb.AppendLine("<div class='card' style='margin-bottom: 12px;'>");
            sb.AppendLine("  <b>Lighting Composition Breakdown:</b> &nbsp;");
            sb.AppendLine($"  <span><b>Active:</b> {report.totalLightsCount}</span> &nbsp;|&nbsp; ");
            sb.AppendLine($"  <span><b>Inactive:</b> {report.inactiveLightsCount}</span> &nbsp;|&nbsp; ");
            sb.AppendLine($"  <span><b>Realtime:</b> {report.realtimeLightsCount}</span> &nbsp;|&nbsp; ");
            sb.AppendLine($"  <span><b>Baked:</b> {report.bakedLightsCount} (0 runtime cost)</span> &nbsp;|&nbsp; ");
            sb.AppendLine($"  <span><b>Mixed:</b> {report.mixedLightsCount}</span> &nbsp;|&nbsp; ");
            sb.AppendLine($"  <span><b>Area/Disc:</b> {report.areaLightsCount}</span> &nbsp;|&nbsp; ");
            sb.AppendLine($"  <span><b>Shadow Casters:</b> {report.shadowCasterLightsCount}</span>");
            sb.AppendLine("</div>");

            // SECTION 3: TOP CULPRITS (RANKED BY SEVERITY SCORE)
            var allCulprits = report.culprits
                .OrderByDescending(c => c.severityScore)
                .ToList();

            sb.AppendLine($"<h2>3. Top Performance Culprits (Ranked by Severity Score - {allCulprits.Count} Total)</h2>");
            if (allCulprits.Count == 0)
            {
                sb.AppendLine("<p>No performance culprits found exceeding target alert thresholds.</p>");
            }
            else
            {
                sb.AppendLine("<table>");
                sb.AppendLine("  <tr><th style='width:30px;'>#</th><th style='width:60px;'>Type</th><th>Culprit / Asset Name</th><th class='num' style='width:65px;'>Severity</th><th>Primary Metric</th><th>Alerts / Flagged Issues</th></tr>");

                for (int i = 0; i < allCulprits.Count; i++)
                {
                    var c = allCulprits[i];
                    string typeBadge = c.type switch
                    {
                        CulpritType.MeshGeometry => "<span class='badge badge-mesh'>Mesh</span>",
                        CulpritType.TextureSize => "<span class='badge badge-tex'>Texture</span>",
                        CulpritType.Light => "<span class='badge badge-light'>Light</span>",
                        _ => "<span class='badge'>Other</span>"
                    };

                    string flagsHtml = (c.flaggedReasons != null && c.flaggedReasons.Count > 0)
                        ? string.Join(" ", c.flaggedReasons.Select(r => $"<span class='alert-pill'>&bull; {Escape(r)}</span>"))
                        : "<span class='subtle'>None</span>";

                    sb.AppendLine($"  <tr><td class='num'>{i + 1}</td><td>{typeBadge}</td><td><b>{Escape(c.name)}</b></td><td class='num'><b>{c.severityScore:F1}</b></td><td>{Escape(c.primaryMetricText)}</td><td>{flagsHtml}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // SECTION 4: SUB-CULPRIT BREAKDOWNS
            sb.AppendLine("<h2>4. Sub-Culprit Breakdowns</h2>");

            // 4A. Heavy Meshes & FBX Models
            var meshCulprits = report.culprits
                .Where(c => c.type == CulpritType.MeshGeometry)
                .OrderByDescending(c => c.severityScore)
                .ToList();

            sb.AppendLine($"<h3>4A. Heavy Meshes &amp; FBX Models ({meshCulprits.Count} Items - Sorted by Severity)</h3>");
            if (meshCulprits.Count == 0)
            {
                sb.AppendLine("<p>No heavy mesh culprits detected.</p>");
            }
            else
            {
                sb.AppendLine("<table>");
                sb.AppendLine("  <tr><th style='width:25px;'>#</th><th>Model / Mesh Name</th><th>FBX Asset Path</th><th class='num'>Occurrences</th><th class='num'>Total Tris</th><th class='num'>Tris/Inst</th><th class='num'>Verts</th><th class='num'>Mats</th><th class='num'>Severity</th><th>Flagged Alerts</th></tr>");

                for (int i = 0; i < meshCulprits.Count; i++)
                {
                    var m = meshCulprits[i];
                    int occ = Mathf.Max(1, m.fbxOccurrences);
                    int perTris = m.triangleCount / occ;
                    int matCount = m.dependencyNames != null ? m.dependencyNames.Count : 0;

                    string flagsHtml = (m.flaggedReasons != null && m.flaggedReasons.Count > 0)
                        ? string.Join(" ", m.flaggedReasons.Select(r => $"<span class='alert-pill'>{Escape(r)}</span>"))
                        : "<span class='subtle'>-</span>";

                    string pathDisplay = !string.IsNullOrEmpty(m.fbxAssetPath) ? Escape(m.fbxAssetPath) : "<span class='subtle'>Scene Mesh</span>";

                    sb.AppendLine($"  <tr><td class='num'>{i + 1}</td><td><b>{Escape(m.name)}</b></td><td class='subtle'>{pathDisplay}</td><td class='num'>{occ}</td><td class='num'><b>{m.triangleCount:N0}</b></td><td class='num'>{perTris:N0}</td><td class='num'>{m.vertexCount:N0}</td><td class='num'>{matCount}</td><td class='num'><b>{m.severityScore:F1}</b></td><td>{flagsHtml}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // 4B. Heavy Textures
            var texCulprits = report.culprits
                .Where(c => c.type == CulpritType.TextureSize)
                .OrderByDescending(c => c.severityScore)
                .ToList();

            sb.AppendLine($"<h3>4B. Heavy Textures ({texCulprits.Count} Items - Sorted by Severity)</h3>");
            if (texCulprits.Count == 0)
            {
                sb.AppendLine("<p>No heavy textures detected.</p>");
            }
            else
            {
                sb.AppendLine("<table>");
                sb.AppendLine("  <tr><th style='width:25px;'>#</th><th>Texture Asset Name</th><th class='num'>Resolution</th><th class='num'>Area Score</th><th class='num'>Severity</th><th>Used in Materials</th></tr>");

                for (int i = 0; i < texCulprits.Count; i++)
                {
                    var t = texCulprits[i];
                    string mats = (t.dependencyNames != null && t.dependencyNames.Count > 0)
                        ? Escape(string.Join(", ", t.dependencyNames))
                        : "<span class='subtle'>Scene</span>";

                    sb.AppendLine($"  <tr><td class='num'>{i + 1}</td><td><b>{Escape(t.name)}</b></td><td class='num'>{t.textureWidth} &times; {t.textureHeight}</td><td class='num'>{t.textureScore:F1} pts</td><td class='num'><b>{t.severityScore:F1}</b></td><td>{mats}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // 4C. Lights & Shadow Casters
            var lightCulprits = report.culprits
                .Where(c => c.type == CulpritType.Light)
                .OrderByDescending(c => c.severityScore)
                .ToList();

            sb.AppendLine($"<h3>4C. Lights &amp; Shadow Casters ({lightCulprits.Count} Groups - Sorted by Severity)</h3>");
            if (lightCulprits.Count == 0)
            {
                sb.AppendLine("<p>No light culprits detected.</p>");
            }
            else
            {
                sb.AppendLine("<table>");
                sb.AppendLine("  <tr><th style='width:25px;'>#</th><th>Group Summary</th><th class='num'>Count</th><th>Type / Mode</th><th>Shadows</th><th class='num'>Range</th><th class='num'>Intensity</th><th>Associated GameObjects</th><th class='num'>Severity</th><th>Alerts</th></tr>");

                for (int i = 0; i < lightCulprits.Count; i++)
                {
                    var l = lightCulprits[i];
                    string mode = l.lightIsBaked ? "Baked" : (l.lightIsMixed ? "Mixed" : "Realtime");
                    string shadow = l.lightShadows != LightShadows.None ? l.lightShadows.ToString() : "None";
                    string goNames = (l.lightNames != null && l.lightNames.Count > 0)
                        ? Escape(string.Join(", ", l.lightNames))
                        : "<span class='subtle'>Light</span>";

                    string flagsHtml = (l.flaggedReasons != null && l.flaggedReasons.Count > 0)
                        ? string.Join(" ", l.flaggedReasons.Select(r => $"<span class='alert-pill'>{Escape(r)}</span>"))
                        : "<span class='subtle'>-</span>";

                    sb.AppendLine($"  <tr><td class='num'>{i + 1}</td><td><b>{Escape(l.name)}</b></td><td class='num'>{l.lightOccurrences}</td><td>{l.lightType} ({mode})</td><td>{shadow}</td><td class='num'>{l.lightRange:F1}m</td><td class='num'>{l.lightIntensity:F2}</td><td class='subtle'>{goNames}</td><td class='num'><b>{l.severityScore:F1}</b></td><td>{flagsHtml}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // SECTION 5: SMART ATLASING & MATERIAL OPTIMIZATIONS
            var atlasGroups = report.atlasSuggestions ?? new List<AtlasGroupSuggestion>();
            int totalSavings = atlasGroups.Sum(g => g.potentialMaterialSavings);

            sb.AppendLine($"<h2>5. Smart Atlasing &amp; Material Optimizations ({atlasGroups.Count} Shader Groups &bull; {totalSavings} Potential Draw Savings)</h2>");

            if (atlasGroups.Count == 0)
            {
                sb.AppendLine("<p>No multi-material atlasing candidates detected.</p>");
            }
            else
            {
                foreach (var g in atlasGroups)
                {
                    sb.AppendLine("<div class='card'>");
                    sb.AppendLine($"  <div style='display:flex; justify-content:space-between; margin-bottom:6px;'>");
                    sb.AppendLine($"    <b>Shader: {Escape(g.shaderName)}</b>");
                    sb.AppendLine($"    <span><b>{g.candidates.Count} Materials</b> &nbsp;|&nbsp; Potential Savings: <span class='badge badge-pass'>{g.potentialMaterialSavings} Draw Calls</span> &nbsp;|&nbsp; Suggested Atlas: <b>{Escape(g.suggestedAtlasSize)}</b></span>");
                    sb.AppendLine($"  </div>");

                    sb.AppendLine("  <table>");
                    sb.AppendLine("    <tr><th style='width:25px;'>#</th><th>Material Name</th><th>Primary Texture</th><th class='num'>Resolution</th><th class='num'>Area Score</th><th class='num'>Scene Objects Using</th></tr>");

                    for (int cIdx = 0; cIdx < g.candidates.Count; cIdx++)
                    {
                        var c = g.candidates[cIdx];
                        string texDim = (c.width > 0 && c.height > 0) ? $"{c.width} &times; {c.height}" : "No Texture";
                        sb.AppendLine($"    <tr><td class='num'>{cIdx + 1}</td><td><b>{Escape(c.materialName)}</b></td><td>{Escape(c.textureName)}</td><td class='num'>{texDim}</td><td class='num'>{c.textureScore:F1} pts</td><td class='num'>{c.rendererUsageCount}</td></tr>");
                    }
                    sb.AppendLine("  </table>");
                    sb.AppendLine("</div>");
                }
            }

            // SECTION 6: VIEWS SPECIFIC REPORT (VIEW-BASED STATS)
            var viewpointStats = report.viewpointStats ?? new List<ViewpointAuditStat>();
            sb.AppendLine($"<h2>6. Views Specific Performance Report ({viewpointStats.Count} Viewpoints Audited)</h2>");

            if (viewpointStats.Count == 0)
            {
                sb.AppendLine("<p class='subtle'>No camera viewpoints were configured for this audit. You can add empty GameObjects or camera vantage points in the Settings tab of Live Scene Auditor to evaluate view-specific performance.</p>");
            }
            else
            {
                // Master Comparison Table
                sb.AppendLine("<p class='subtle'>Evaluation of geometry, materials, and lights rendered within the camera frustum for each configured vantage point.</p>");
                sb.AppendLine("<table>");
                sb.AppendLine("  <tr><th style='width:25px;'>#</th><th>Viewpoint Name</th><th>Position / Orientation</th><th class='num'>Visible Tris</th><th class='num'>Visible Verts</th><th class='num'>Unique Mats</th><th class='num'>Visible Lights</th><th>Top Culprit in View</th><th style='text-align:center;'>Status</th></tr>");

                for (int v = 0; v < viewpointStats.Count; v++)
                {
                    var vp = viewpointStats[v];
                    string statusClass = vp.isOverBudget ? "badge-fail" : (vp.healthPercentage >= 75f ? "badge-warn" : "badge-pass");
                    string statusText = vp.statusSummary;

                    float triRatio = profile.maxTriangles > 0 ? ((float)vp.visibleTriangles / profile.maxTriangles * 100f) : 0f;
                    float vertRatio = profile.maxVertices > 0 ? ((float)vp.visibleVertices / profile.maxVertices * 100f) : 0f;

                    string posStr = $"({vp.position.x:F1}, {vp.position.y:F1}, {vp.position.z:F1})";
                    string rotStr = $"Pitch: {vp.rotationEuler.x:F0}&deg;, Yaw: {vp.rotationEuler.y:F0}&deg;";

                    string topCulpritStr = !string.IsNullOrEmpty(vp.topCulpritName) && vp.topCulpritName != "None"
                        ? $"<b>{Escape(vp.topCulpritName)}</b> ({vp.topCulpritTris:N0} tris)"
                        : "<span class='subtle'>-</span>";

                    string lightBreakdown = $"{vp.visibleLightsCount} lights (Dir: {vp.directionalLightsCount}, Pt: {vp.pointLightsCount}, Spt: {vp.spotLightsCount})";

                    sb.AppendLine($"  <tr><td class='num'>{v + 1}</td><td><b>{Escape(vp.viewpointName)}</b></td><td class='subtle'>{posStr}<br/>{rotStr}</td><td class='num'><b>{vp.visibleTriangles:N0}</b><br/><span class='subtle'>{triRatio:F1}% limit</span></td><td class='num'>{vp.visibleVertices:N0}<br/><span class='subtle'>{vertRatio:F1}% limit</span></td><td class='num'><b>{vp.uniqueMaterialsCount}</b></td><td class='num' title='{Escape(lightBreakdown)}'>{vp.visibleLightsCount}</td><td>{topCulpritStr}</td><td style='text-align:center;'><span class='badge {statusClass}'>{Escape(statusText)}</span></td></tr>");
                }
                sb.AppendLine("</table>");

                // Individual View Breakdown Cards
                sb.AppendLine("<h3>Vantage Point Frustum Details:</h3>");
                foreach (var vp in viewpointStats)
                {
                    string cardStatusClass = vp.isOverBudget ? "status-fail" : "status-pass";
                    string badgeClass = vp.isOverBudget ? "badge-fail" : (vp.healthPercentage >= 75f ? "badge-warn" : "badge-pass");

                    sb.AppendLine($"<div class='card' style='margin-bottom:8px;'>");
                    sb.AppendLine($"  <div style='display:flex; justify-content:space-between; align-items:center; margin-bottom:6px;'>");
                    sb.AppendLine($"    <b>Viewpoint: {Escape(vp.viewpointName)}</b> &nbsp;<span class='subtle'>Position: ({vp.position.x:F1}, {vp.position.y:F1}, {vp.position.z:F1}) | Rotation: ({vp.rotationEuler.x:F0}&deg;, {vp.rotationEuler.y:F0}&deg;, {vp.rotationEuler.z:F0}&deg;)</span>");
                    sb.AppendLine($"    <span class='badge {badgeClass}'>{Escape(vp.statusSummary)}</span>");
                    sb.AppendLine($"  </div>");

                    sb.AppendLine("  <table>");
                    sb.AppendLine("    <tr><th>Metric</th><th class='num'>Value in Frustum</th><th class='num'>Budget Target</th><th class='num'>Usage %</th><th style='text-align:center;'>Status</th><th>Notes</th></tr>");
                    AppendMetricRow(sb, "Frustum Triangles", vp.visibleTriangles, profile.maxTriangles, "tris", "Triangles within camera frustum");
                    AppendMetricRow(sb, "Frustum Vertices", vp.visibleVertices, profile.maxVertices, "verts", "Vertices within camera frustum");
                    AppendMetricRow(sb, "Unique Materials in View", vp.uniqueMaterialsCount, profile.maxMaterialsInScene, "mats", "Unique material assets drawn in this view");
                    AppendMetricRow(sb, "Visible Lights", vp.visibleLightsCount, profile.maxTotalLights, "lights", $"Dir: {vp.directionalLightsCount}, Pt: {vp.pointLightsCount}, Spot: {vp.spotLightsCount}, Shadow Casters: {vp.shadowCasterLightsCount}");
                    sb.AppendLine("  </table>");

                    if (!string.IsNullOrEmpty(vp.topCulpritName) && vp.topCulpritName != "None")
                    {
                        sb.AppendLine($"  <div style='font-size:9px;'><b>Top Culprit in Vantage Point:</b> {Escape(vp.topCulpritName)} &bull; <b>{vp.topCulpritTris:N0} Triangles</b></div>");
                    }
                    sb.AppendLine("</div>");
                }
            }

            // Footer
            sb.AppendLine("<div class='footer'>");
            sb.AppendLine($"  Generated by <b>Live Scene Auditor v1.0</b> &bull; {Escape(Application.productName)} &bull; {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("</div>");

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        private static void AppendMetricRow(StringBuilder sb, string label, int current, int budget, string unit, string notes)
        {
            float ratio = budget > 0 ? ((float)current / budget) : 0f;
            float percent = ratio * 100f;
            string badgeClass = (ratio >= 1.0f) ? "badge-fail" : (ratio >= 0.75f ? "badge-warn" : "badge-pass");
            string statusText = (ratio >= 1.0f) ? "OVER BUDGET" : (ratio >= 0.75f ? "WARNING" : "OK");

            sb.AppendLine($"  <tr><td><b>{Escape(label)}</b></td><td class='num'><b>{current:N0}</b> {unit}</td><td class='num'>{budget:N0} {unit}</td><td class='num'>{percent:F1}%</td><td style='text-align:center;'><span class='badge {badgeClass}'>{statusText}</span></td><td class='subtle'>{Escape(notes)}</td></tr>");
        }

        private static void AppendMetricFloatRow(StringBuilder sb, string label, float current, float budget, string unit, string notes)
        {
            float ratio = budget > 0f ? (current / budget) : 0f;
            float percent = ratio * 100f;
            string badgeClass = (ratio >= 1.0f) ? "badge-fail" : (ratio >= 0.75f ? "badge-warn" : "badge-pass");
            string statusText = (ratio >= 1.0f) ? "OVER BUDGET" : (ratio >= 0.75f ? "WARNING" : "OK");

            sb.AppendLine($"  <tr><td><b>{Escape(label)}</b></td><td class='num'><b>{current:F1}</b> {unit}</td><td class='num'>{budget:F1} {unit}</td><td class='num'>{percent:F1}%</td><td style='text-align:center;'><span class='badge {badgeClass}'>{statusText}</span></td><td class='subtle'>{Escape(notes)}</td></tr>");
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return System.Net.WebUtility.HtmlEncode(s);
        }

        // ==========================================
        // STANDALONE PURE C# PDF GENERATOR FALLBACK
        // Standard PDF 1.4 Vector File Generator
        // ==========================================
        private static bool GenerateFallbackPdf(SceneAuditReport report, BudgetProfile profile, bool includeInactive, string targetPdfPath)
        {
            try
            {
                using FileStream fs = new FileStream(targetPdfPath, FileMode.Create, FileAccess.Write);
                using StreamWriter writer = new StreamWriter(fs, Encoding.ASCII);

                List<long> offsets = new List<long>();

                void WriteObject(string content)
                {
                    writer.Flush();
                    offsets.Add(fs.Position);
                    writer.Write(content);
                    writer.Flush();
                }

                writer.Write("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
                writer.Flush();

                // Object 1: Catalog
                WriteObject("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

                // Object 2: Pages container
                WriteObject("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

                // Object 3: Page definition
                WriteObject("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>\nendobj\n");

                // Object 4: Regular Font (Helvetica)
                WriteObject("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

                // Object 5: Bold Font (Helvetica-Bold)
                WriteObject("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>\nendobj\n");

                // Object 6: Stream Contents with real drawn tables and rows
                StringBuilder cs = new StringBuilder(8192);

                // Draw Top Header Banner
                cs.Append("0.1 0.25 0.65 rg 40 790 515 35 re f\n"); // Header rect
                cs.Append("1 1 1 rg BT /F2 14 Tf 50 802 Td (Live Scene Auditor - Performance Audit Report) Tj ET\n");

                // Subtitle
                string sceneInfo = $"Project: {SanitizeAscii(Application.productName)} | Scene: {SanitizeAscii(SceneManager.GetActiveScene().name)} | Date: {DateTime.Now:yyyy-MM-dd}";
                cs.Append("0.2 0.2 0.2 rg BT /F1 9 Tf 40 775 Td (" + EscapePdf(sceneInfo) + ") Tj ET\n");

                // Health Status Box
                if (report.isOverBudget)
                {
                    cs.Append("1 0.9 0.9 rg 40 735 515 26 re f 0.8 0.2 0.2 RG 40 735 515 26 re S\n");
                    cs.Append("0.6 0.1 0.1 rg BT /F2 10 Tf 50 744 Td (FAIL - OVER BUDGET (" + $"{report.overallHealthPercentage:F0}" + "%)) Tj ET\n");
                }
                else
                {
                    cs.Append("0.9 1 0.9 rg 40 735 515 26 re f 0.2 0.7 0.2 RG 40 735 515 26 re S\n");
                    cs.Append("0.1 0.5 0.1 rg BT /F2 10 Tf 50 744 Td (PASS - WITHIN BUDGET (" + $"{report.overallHealthPercentage:F0}" + "%)) Tj ET\n");
                }

                // Section 1 Header
                cs.Append("0.15 0.35 0.7 rg BT /F2 11 Tf 40 715 Td (1. Scene Performance Overview & Budgets) Tj ET\n");

                // Table 1 Header
                float tableY = 690;
                cs.Append("0.15 0.35 0.7 rg 40 " + tableY + " 515 18 re f\n");
                cs.Append("1 1 1 rg BT /F2 8.5 Tf 45 " + (tableY + 5) + " Td (METRIC) Tj 220 " + (tableY + 5) + " Td (SCENE VALUE) Tj 320 " + (tableY + 5) + " Td (BUDGET) Tj 410 " + (tableY + 5) + " Td (USAGE) Tj 480 " + (tableY + 5) + " Td (STATUS) Tj ET\n");

                (string label, string val, string bgt, float ratio)[] rows = new[]
                {
                    ("Total Scene Triangles", $"{report.totalSceneTriangles:N0}", $"{profile.maxTriangles:N0}", (float)report.totalSceneTriangles / Mathf.Max(1, profile.maxTriangles)),
                    ("Total Scene Vertices", $"{report.totalSceneVertices:N0}", $"{profile.maxVertices:N0}", (float)report.totalSceneVertices / Mathf.Max(1, profile.maxVertices)),
                    ("Unique Scene Materials", $"{report.uniqueMaterialsCount}", $"{profile.maxMaterialsInScene}", (float)report.uniqueMaterialsCount / Mathf.Max(1, profile.maxMaterialsInScene)),
                    ("Scene FBX Models", $"{report.fbxModelCount}", $"{profile.maxFbxInScene}", (float)report.fbxModelCount / Mathf.Max(1, profile.maxFbxInScene)),
                    ("Texture Area Score", $"{report.textureAreaScore:F1}", $"{profile.maxTextureScore:F1}", report.textureAreaScore / Mathf.Max(0.1f, profile.maxTextureScore)),
                    ("Total Scene Lights", $"{report.totalLightsCount}", $"{profile.maxTotalLights}", (float)report.totalLightsCount / Mathf.Max(1, profile.maxTotalLights)),
                    ("Realtime Shadow Casters", $"{report.shadowCasterLightsCount}", $"{profile.maxRealtimeShadowCasters}", (float)report.shadowCasterLightsCount / Mathf.Max(1, profile.maxRealtimeShadowCasters)),
                };

                for (int r = 0; r < rows.Length; r++)
                {
                    tableY -= 16;
                    var item = rows[r];
                    if (r % 2 == 1)
                    {
                        cs.Append("0.96 0.97 0.98 rg 40 " + tableY + " 515 16 re f\n");
                    }
                    cs.Append("0.85 0.85 0.85 RG 40 " + tableY + " 515 16 re S\n");

                    string status = item.ratio >= 1.0f ? "OVER" : (item.ratio >= 0.75f ? "WARN" : "OK");
                    cs.Append("0.1 0.1 0.1 rg BT /F1 8 Tf 45 " + (tableY + 4) + " Td (" + EscapePdf(item.label) + ") Tj 220 " + (tableY + 4) + " Td (" + EscapePdf(item.val) + ") Tj 320 " + (tableY + 4) + " Td (" + EscapePdf(item.bgt) + ") Tj 410 " + (tableY + 4) + " Td (" + $"{item.ratio * 100f:F1}%" + ") Tj ");
                    
                    if (item.ratio >= 1.0f) cs.Append("/F2 8 Tf 0.8 0.1 0.1 rg ");
                    else cs.Append("/F1 8 Tf 0.1 0.5 0.1 rg ");

                    cs.Append("480 " + (tableY + 4) + " Td (" + status + ") Tj ET\n");
                }

                // Section 2: Top Culprits
                tableY -= 30;
                cs.Append("0.15 0.35 0.7 rg BT /F2 11 Tf 40 " + tableY + " Td (2. Top Performance Culprits (Ranked by Severity)) Tj ET\n");

                tableY -= 20;
                cs.Append("0.15 0.35 0.7 rg 40 " + tableY + " 515 18 re f\n");
                cs.Append("1 1 1 rg BT /F2 8.5 Tf 45 " + (tableY + 5) + " Td (#) Tj 65 " + (tableY + 5) + " Td (TYPE) Tj 115 " + (tableY + 5) + " Td (NAME / ASSET) Tj 340 " + (tableY + 5) + " Td (METRIC) Tj 480 " + (tableY + 5) + " Td (SEVERITY) Tj ET\n");

                int culpritCountToTake = (report.viewpointStats != null && report.viewpointStats.Count > 0) ? 6 : 14;
                var topCulprits = report.culprits.OrderByDescending(c => c.severityScore).Take(culpritCountToTake).ToList();
                for (int c = 0; c < topCulprits.Count; c++)
                {
                    tableY -= 16;
                    var item = topCulprits[c];
                    if (c % 2 == 1)
                    {
                        cs.Append("0.96 0.97 0.98 rg 40 " + tableY + " 515 16 re f\n");
                    }
                    cs.Append("0.85 0.85 0.85 RG 40 " + tableY + " 515 16 re S\n");

                    string tName = item.type switch { CulpritType.MeshGeometry => "Mesh", CulpritType.TextureSize => "Texture", CulpritType.Light => "Light", _ => "Other" };
                    string cName = SanitizeAscii(item.name);
                    if (cName.Length > 36) cName = cName.Substring(0, 33) + "...";

                    string cMetric = SanitizeAscii(item.primaryMetricText);
                    if (cMetric.Length > 24) cMetric = cMetric.Substring(0, 21) + "...";

                    cs.Append("0.1 0.1 0.1 rg BT /F1 8 Tf 45 " + (tableY + 4) + " Td (" + (c + 1) + ") Tj 65 " + (tableY + 4) + " Td (" + tName + ") Tj 115 " + (tableY + 4) + " Td (" + EscapePdf(cName) + ") Tj 340 " + (tableY + 4) + " Td (" + EscapePdf(cMetric) + ") Tj /F2 8 Tf 480 " + (tableY + 4) + " Td (" + $"{item.severityScore:F1}" + ") Tj ET\n");
                }

                // Section 3: Views Specific Report (if viewpoints are configured)
                if (report.viewpointStats != null && report.viewpointStats.Count > 0 && tableY > 100)
                {
                    tableY -= 26;
                    cs.Append("0.15 0.35 0.7 rg BT /F2 11 Tf 40 " + tableY + " Td (3. Views Specific Report (Vantage Points)) Tj ET\n");

                    tableY -= 18;
                    cs.Append("0.15 0.35 0.7 rg 40 " + tableY + " 515 18 re f\n");
                    cs.Append("1 1 1 rg BT /F2 8.5 Tf 45 " + (tableY + 5) + " Td (#) Tj 65 " + (tableY + 5) + " Td (VIEWPOINT NAME) Tj 220 " + (tableY + 5) + " Td (TRIS) Tj 310 " + (tableY + 5) + " Td (VERTS) Tj 390 " + (tableY + 5) + " Td (MATS) Tj 440 " + (tableY + 5) + " Td (LIGHTS) Tj 490 " + (tableY + 5) + " Td (STATUS) Tj ET\n");

                    for (int v = 0; v < report.viewpointStats.Count && tableY > 60; v++)
                    {
                        tableY -= 16;
                        var vp = report.viewpointStats[v];
                        if (v % 2 == 1) cs.Append("0.96 0.97 0.98 rg 40 " + tableY + " 515 16 re f\n");
                        cs.Append("0.85 0.85 0.85 RG 40 " + tableY + " 515 16 re S\n");

                        string vName = SanitizeAscii(vp.viewpointName);
                        if (vName.Length > 24) vName = vName.Substring(0, 21) + "...";
                        string statStr = vp.isOverBudget ? "OVER" : "PASS";

                        cs.Append("0.1 0.1 0.1 rg BT /F1 8 Tf 45 " + (tableY + 4) + " Td (" + (v + 1) + ") Tj 65 " + (tableY + 4) + " Td (" + EscapePdf(vName) + ") Tj 220 " + (tableY + 4) + " Td (" + $"{vp.visibleTriangles:N0}" + ") Tj 310 " + (tableY + 4) + " Td (" + $"{vp.visibleVertices:N0}" + ") Tj 390 " + (tableY + 4) + " Td (" + vp.uniqueMaterialsCount + ") Tj 440 " + (tableY + 4) + " Td (" + vp.visibleLightsCount + ") Tj ");
                        if (vp.isOverBudget) cs.Append("/F2 8 Tf 0.8 0.1 0.1 rg ");
                        else cs.Append("/F1 8 Tf 0.1 0.5 0.1 rg ");
                        cs.Append("490 " + (tableY + 4) + " Td (" + statStr + ") Tj ET\n");
                    }
                }

                // Footer
                cs.Append("0.5 0.5 0.5 rg BT /F1 7.5 Tf 40 30 Td (Generated by Live Scene Auditor v1.0 | " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ") Tj ET\n");

                byte[] streamBytes = Encoding.ASCII.GetBytes(cs.ToString());
                WriteObject($"6 0 obj\n<< /Length {streamBytes.Length} >>\nstream\n{cs}endstream\nendobj\n");

                // Cross-reference table
                writer.Flush();
                long startXref = fs.Position;
                writer.Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
                foreach (long off in offsets)
                {
                    writer.Write($"{off:D10} 00000 n \n");
                }

                writer.Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{startXref}\n%%EOF\n");
                writer.Flush();

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Live Scene Auditor] Pure C# PDF generation error: {ex.Message}");
                return false;
            }
        }

        private static string EscapePdf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        }

        private static string SanitizeAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 32 && c <= 126) sb.Append(c);
                else sb.Append(' ');
            }
            return sb.ToString();
        }
    }
}
