using System;
using System.Collections.Generic;
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
        public string PrettyContent { get; set; } // 展示用的漂亮的 JSON
        public string RawContent { get; set; }    // 原始文本
    }

    public static class LogParser
    {
        // 宽松的 DoType 正则
        private static Regex _doTypeRegex = new Regex(
            @"(DoType\d+)\s+.*?(?:playerId|pid|player_id)\s*[:=]\s*(\d+).*?data\s*=\s*(.*)", 
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // PID 提取正则 (增强版，支持 Pid:123 和 pid=123)
        private static Regex _pidRegex = new Regex(
            @"(?:pid|playerId|player_id|PlayerID)\s*[:=]\s*(\d+)", 
            RegexOptions.IgnoreCase);

        // 智能分块 (保持不变)
        public static List<string> SmartSplit(string fullText)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(fullText)) return chunks;
            fullText = fullText.Replace("\r\n", "\n").Replace("}{\"PN\":", "}\n{\"PN\":");
            var lines = fullText.Split('\n');
            string currentBuffer = "";
            foreach (var line in lines)
            {
                string trimLine = line.Trim();
                if (trimLine.StartsWith("{\"PN\":") || trimLine.StartsWith("DoType") || trimLine.Contains("DoType"))
                {
                    if (!string.IsNullOrWhiteSpace(currentBuffer)) chunks.Add(currentBuffer);
                    currentBuffer = line;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(currentBuffer)) currentBuffer = line;
                    else currentBuffer += "\n" + line;
                }
            }
            if (!string.IsNullOrWhiteSpace(currentBuffer)) chunks.Add(currentBuffer);
            return chunks;
        }

        public static GameLogItem Parse(string content, int index)
        {
            var item = new GameLogItem
            {
                Id = index,
                RawContent = content,
                Category = "System",
                PID = ExtractPid(content), // 1. 无论什么日志，先暴力提取 PID
                HeaderInfo = $"[#{index}] [System]"
            };

            try
            {
                string trimContent = content.Trim();

                // --- Case 1: DoType ---
                var match = _doTypeRegex.Match(trimContent);
                if (match.Success)
                {
                    item.Category = match.Groups[1].Value; 
                    // 如果正则没抓到 PID，就用暴力提取的
                    if (string.IsNullOrEmpty(item.PID)) item.PID = match.Groups[2].Value;
                    
                    string jsonPart = match.Groups[3].Value;
                    int lastBrace = jsonPart.LastIndexOf('}');
                    if (lastBrace != -1 && lastBrace < jsonPart.Length - 1) jsonPart = jsonPart.Substring(0, lastBrace + 1);

                    item.PrettyContent = SmartFormatJson(jsonPart);
                }
                // --- Case 2: JSON Log ---
                else if (trimContent.StartsWith("{") && trimContent.Contains("\"msg\":"))
                {
                    try
                    {
                        var root = JObject.Parse(trimContent);
                        string msg = root["msg"]?.ToString() ?? "";
                        string time = root["time"]?.ToString() ?? "";
                        
                        // 更新 PID (JSON 里的优先级更高)
                        string jsonPid = ExtractPid(msg);
                        if (!string.IsNullOrEmpty(jsonPid)) item.PID = jsonPid;

                        // === 特殊处理 1: 登录数据 ===
                        if (msg.Contains("OnGameServerLoginBattlefield"))
                        {
                            item.Category = "LoginBattlefield";
                            int start = msg.IndexOf("{");
                            if (start != -1) item.PrettyContent = SmartFormatJson(msg.Substring(start));
                            else item.PrettyContent = msg;
                        }
                        // === 特殊处理 2: SendGrpc (文本转 JSON) ===
                        else if (msg.Contains("SendGrpc"))
                        {
                            item.Category = "SendGrpc";
                            // 提取 Info 之后的内容并手动转成 JSON 树
                            int infoIndex = msg.IndexOf("Info:");
                            if (infoIndex != -1)
                            {
                                string infoText = msg.Substring(infoIndex + 5);
                                // 调用自定义解析器
                                JArray parsedArray = ParseSendGrpcInfo(infoText);
                                item.PrettyContent = parsedArray.ToString(Formatting.Indented);
                            }
                            else
                            {
                                item.PrettyContent = msg; 
                            }
                        }
                        else
                        {
                            item.Category = "JsonLog";
                            item.PrettyContent = SmartFormatJson(trimContent);
                        }
                    }
                    catch
                    {
                        item.Category = "JsonLog";
                        item.PrettyContent = trimContent;
                    }
                }
                // --- Case 3: Error ---
                else if (content.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || 
                         content.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    item.Category = "Error";
                    item.PrettyContent = content;
                }
                else
                {
                    item.PrettyContent = content;
                }

                // 生成最终标题
                string pidStr = string.IsNullOrEmpty(item.PID) ? "" : $" [PID: {item.PID}]";
                item.HeaderInfo = $"[#{index}] [{item.Category}]{pidStr}";
            }
            catch
            {
                item.Category = "ParseError";
                item.PrettyContent = content;
            }

            return item;
        }

        // === 强力 PID 提取器 ===
        private static string ExtractPid(string text)
        {
            var match = _pidRegex.Match(text);
            return match.Success ? match.Groups[1].Value : "";
        }

        // === 核心功能：把 SendGrpc 的乱文本转成 JSON 树 ===
        private static JArray ParseSendGrpcInfo(string text)
        {
            // 文本结构是: 
            // GoldDashWarItem Details: key=val, key=val...
            // GoldDashWarItem Details: key=val, key=val...
            
            JArray array = new JArray();
            var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.Trim().StartsWith("GoldDashWarItem Details:"))
                {
                    JObject obj = new JObject();
                    // 去掉前缀
                    string data = line.Replace("GoldDashWarItem Details:", "").Trim();
                    // 按逗号分割 (注意：这里假设 value 里没有逗号，如果有复杂嵌套可能需要更复杂的逻辑)
                    var parts = data.Split(',');
                    
                    foreach (var part in parts)
                    {
                        var kv = part.Split('=');
                        if (kv.Length == 2)
                        {
                            string key = kv[0].Trim();
                            string val = kv[1].Trim();
                            // 尝试转数字
                            if (long.TryParse(val, out long num)) obj[key] = num;
                            else obj[key] = val;
                        }
                    }
                    array.Add(obj);
                }
            }
            return array;
        }

        // === 递归展开 JSON (保持不变) ===
        private static string SmartFormatJson(string input)
        {
            try {
                var token = JToken.Parse(input);
                RecursivelyExpandJson(token);
                return token.ToString(Formatting.Indented);
            } catch { return input; }
        }

        private static void RecursivelyExpandJson(JToken token)
        {
            if (token.Type == JTokenType.Object)
                foreach (var child in token.Children<JProperty>()) RecursivelyExpandJson(child.Value);
            else if (token.Type == JTokenType.Array)
                foreach (var child in token.Children()) RecursivelyExpandJson(child);
            else if (token.Type == JTokenType.String)
            {
                string str = token.ToString();
                if ((str.TrimStart().StartsWith("{") && str.TrimEnd().EndsWith("}")) || 
                    (str.TrimStart().StartsWith("[") && str.TrimEnd().EndsWith("]")))
                {
                    try {
                        var inner = JToken.Parse(str);
                        RecursivelyExpandJson(inner);
                        if (token.Parent is JProperty p) p.Value = inner;
                    } catch {}
                }
            }
        }
    }
}