using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LogParserTool
{
    public class GameLogItem
    {
        public int Id { get; set; }
        public string Category { get; set; }
        public string PID { get; set; }
        public string HeaderInfo { get; set; }
        public string PrettyContent { get; set; }
        public string RawContent { get; set; }
    }

    public static class LogParser
    {
        private static Regex _doTypeRegex = new Regex(
            @"(DoType\d+)\s+.*?(?:playerId|pid|player_id)\s*[:=]\s*(\d+).*?data\s*=\s*(.*)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static Regex _pidRegex = new Regex(
            @"(?:[""']?(?:pid|playerId|player_id|PlayerID)[""']?)\s*[:=]\s*(\d+)",
            RegexOptions.IgnoreCase);

        // === 【关键修复】智能分块逻辑 ===
        public static List<string> SmartSplit(string fullText)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(fullText)) return chunks;
            
            // 预处理：修复 JSON 粘连
            fullText = fullText.Replace("\r\n", "\n").Replace("}{\"PN\":", "}\n{\"PN\":");
            var lines = fullText.Split('\n');
            string currentBuffer = "";
            
            foreach (var line in lines)
            {
                string trimLine = line.Trim();
                if (string.IsNullOrEmpty(trimLine)) continue; // 跳过空行防止干扰

                // 判断是否为新日志的开始
                bool isNewLog = 
                    // 1. 结构化日志特征
                    trimLine.StartsWith("{\"PN\":") || 
                    trimLine.StartsWith("DoType") || 
                    trimLine.StartsWith("[") || 
                    trimLine.StartsWith("msg:") ||
                    
                    // 2. 常见系统日志特征 (修复点：防止这些被吞掉)
                    trimLine.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                    trimLine.StartsWith("Exception", StringComparison.OrdinalIgnoreCase) ||
                    trimLine.StartsWith("Mono") ||
                    trimLine.StartsWith("Init") ||
                    trimLine.StartsWith("Shader") ||
                    trimLine.StartsWith("Crash") ||
                    
                    // 3. 时间戳特征 (例如 2026-02...)
                    (trimLine.Length > 4 && char.IsDigit(trimLine[0]) && char.IsDigit(trimLine[1]) && char.IsDigit(trimLine[2]) && char.IsDigit(trimLine[3]));

                if (isNewLog)
                {
                    if (!string.IsNullOrWhiteSpace(currentBuffer)) chunks.Add(currentBuffer);
                    currentBuffer = line;
                }
                else
                {
                    // 不是新日志头，拼接到上一条后面
                    if (string.IsNullOrWhiteSpace(currentBuffer)) currentBuffer = line;
                    else currentBuffer += "\n" + line;
                }
            }
            // 收尾
            if (!string.IsNullOrWhiteSpace(currentBuffer)) chunks.Add(currentBuffer);
            return chunks;
        }

        public static GameLogItem Parse(string content, int index)
        {
            var item = new GameLogItem
            {
                Id = index,
                RawContent = content,
                Category = "System", // 默认为 System
                PID = ExtractPid(content),
                HeaderInfo = $"[#{index}] [System]"
            };

            try
            {
                string trimContent = content.Trim();

                // Case 1: DoType
                var match = _doTypeRegex.Match(trimContent);
                if (match.Success)
                {
                    item.Category = match.Groups[1].Value.Trim();
                    if (string.IsNullOrEmpty(item.PID)) item.PID = match.Groups[2].Value;
                    string jsonPart = match.Groups[3].Value;
                    int lastBrace = jsonPart.LastIndexOf('}');
                    if (lastBrace != -1) jsonPart = jsonPart.Substring(0, lastBrace + 1);
                    item.PrettyContent = SmartFormatJson(jsonPart);
                }
                // Case 2: 纯文本 SendGrpcMsg
                else if (trimContent.Contains("SendGrpcMsg") || (trimContent.Contains("SendGrpc") && trimContent.Contains("Info:")))
                {
                    item.Category = "SendGrpc";
                    if (string.IsNullOrEmpty(item.PID)) item.PID = ExtractPid(trimContent);
                    item.PrettyContent = ParseSendGrpcContent(trimContent);
                }
                // Case 3: JSON
                else if (trimContent.StartsWith("{"))
                {
                    item.PrettyContent = SmartFormatJson(trimContent);
                    try
                    {
                        var root = JObject.Parse(trimContent);
                        string msg = root["msg"]?.ToString() ?? "";
                        
                        string innerPid = ExtractPid(msg);
                        if (!string.IsNullOrEmpty(innerPid)) item.PID = innerPid;

                        if (msg.Contains("SendGrpcMsg") || (msg.Contains("SendGrpc") && msg.Contains("Info:")))
                        {
                            item.Category = "SendGrpc";
                            string parsedGrpc = ParseSendGrpcContent(msg);
                            item.PrettyContent += "\n\n// === SendGrpc Parsed ===\n" + parsedGrpc;
                        }
                        else
                        {
                            int braceIndex = msg.IndexOf('{');
                            if (braceIndex > 0)
                            {
                                string prefix = msg.Substring(0, braceIndex).Trim();
                                prefix = prefix.Replace("[GRPC]", "").Replace(":", "").Trim();
                                item.Category = string.IsNullOrEmpty(prefix) ? "JsonLog" : prefix;
                            }
                            else
                            {
                                item.Category = "JsonLog";
                            }
                        }
                    }
                    catch { item.Category = "JsonRaw"; }
                }
                // Case 4: Error 检测 (放在最后，防止覆盖更精确的分类)
                else if (content.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || 
                         content.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    item.Category = "Error";
                    item.PrettyContent = content;
                }
                else
                {
                    item.PrettyContent = content; // 普通日志
                }

                string pidStr = string.IsNullOrEmpty(item.PID) ? "" : $" [PID:{item.PID}]";
                item.HeaderInfo = $"[#{index}] [{item.Category}]{pidStr}";
            }
            catch
            {
                item.Category = "ParseError";
                item.PrettyContent = content;
            }

            return item;
        }

        // SendGrpc 解析 (含 \n 修复)
        private static string ParseSendGrpcContent(string text)
        {
            try
            {
                int infoIndex = text.IndexOf("Info:");
                if (infoIndex == -1) return text;

                string headerPart = text.Substring(0, infoIndex).Trim();
                string dataPart = text.Substring(infoIndex + 5).Trim();
                
                // 修复字面量换行符
                dataPart = dataPart.Replace("\\n", "\n");

                JObject resultObj = new JObject();
                resultObj["_Header"] = headerPart;
                
                JArray itemsArray = new JArray();
                using (StringReader reader = new StringReader(dataPart))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string trimLine = line.Trim();
                        int detIdx = trimLine.IndexOf("Details:");
                        if (detIdx != -1)
                        {
                            JObject itemObj = new JObject();
                            string kvStr = trimLine.Substring(detIdx + 8).Trim();
                            var parts = kvStr.Split(',');
                            foreach (var part in parts)
                            {
                                var kv = part.Split('=');
                                if (kv.Length >= 2)
                                {
                                    string key = kv[0].Trim();
                                    string val = string.Join("=", kv.Skip(1)).Trim();
                                    if (long.TryParse(val, out long numVal)) itemObj[key] = numVal;
                                    else itemObj[key] = val;
                                }
                            }
                            itemsArray.Add(itemObj);
                        }
                    }
                }
                resultObj["Items"] = itemsArray;
                resultObj["_ItemCount"] = itemsArray.Count;
                return resultObj.ToString(Formatting.Indented);
            }
            catch { return text; }
        }

        private static string ExtractPid(string text)
        {
            var match = _pidRegex.Match(text);
            return match.Success ? match.Groups[1].Value : "";
        }

        private static string SmartFormatJson(string input)
        {
            try { var t = JToken.Parse(input); RecursivelyExpandJson(t); return t.ToString(Formatting.Indented); }
            catch { return input; }
        }

        private static void RecursivelyExpandJson(JToken token)
        {
            if (token.Type == JTokenType.Object) foreach (var c in token.Children<JProperty>()) RecursivelyExpandJson(c.Value);
            else if (token.Type == JTokenType.Array) foreach (var c in token.Children()) RecursivelyExpandJson(c);
            else if (token.Type == JTokenType.String)
            {
                string str = token.ToString();
                if (str.Contains("{") && str.Contains("}"))
                {
                    try { var i = JToken.Parse(str); RecursivelyExpandJson(i); if (token.Parent is JProperty p) p.Value = i; return; } catch { }
                    try {
                        int f = str.IndexOf('{'), l = str.LastIndexOf('}');
                        if (f != -1 && l > f) {
                            var i = JToken.Parse(str.Substring(f, l - f + 1));
                            RecursivelyExpandJson(i);
                            JObject w = new JObject();
                            string pre = str.Substring(0, f).Trim();
                            if (!string.IsNullOrEmpty(pre)) w["_desc"] = pre;
                            w["_data"] = i;
                            if (token.Parent is JProperty p) p.Value = w;
                        }
                    } catch { }
                }
            }
        }
    }
}