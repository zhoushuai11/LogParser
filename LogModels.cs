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
        public string PrettyContent { get; set; }
        public string RawContent { get; set; }
    }

    public static class LogParser
    {
        // 宽松的 DoType 正则
        private static Regex _doTypeRegex = new Regex(
            @"(DoType\d+)\s+.*?(?:playerId|pid|player_id)\s*[:=]\s*(\d+).*?data\s*=\s*(.*)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // 强力 PID 正则
        private static Regex _pidRegex = new Regex(
            @"(?:[""']?(?:pid|playerId|player_id|PlayerID)[""']?)\s*[:=]\s*(\d+)",
            RegexOptions.IgnoreCase);

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
                if (trimLine.StartsWith("{\"PN\":") || trimLine.StartsWith("DoType") || trimLine.StartsWith("["))
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
                PID = ExtractPid(content),
                HeaderInfo = $"[#{index}] [System]"
            };

            try
            {
                string trimContent = content.Trim();

                // Case 1: DoType 类型
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
                // Case 2: SendGrpcMsg (特殊处理，优先于 JSON 判断，防止被当成普通文本)
                else if (trimContent.Contains("SendGrpcMsg") || (trimContent.Contains("SendGrpc") && trimContent.Contains("Info:")))
                {
                    item.Category = "SendGrpc";
                    // 尝试从文本里提取 PID
                    if (string.IsNullOrEmpty(item.PID)) item.PID = ExtractPid(trimContent);
                    // 专门的解析器
                    item.PrettyContent = ParseSendGrpcContent(trimContent);
                }
                // Case 3: JSON 类型 (含 msg 嵌套)
                else if (trimContent.StartsWith("{"))
                {
                    item.PrettyContent = SmartFormatJson(trimContent); 
                    
                    try
                    {
                        var root = JObject.Parse(trimContent);
                        string msg = root["msg"]?.ToString() ?? "";
                        
                        string innerPid = ExtractPid(msg);
                        if (!string.IsNullOrEmpty(innerPid)) item.PID = innerPid;

                        // 再次检查 msg 内部是不是 SendGrpc
                        if (msg.Contains("SendGrpcMsg") || (msg.Contains("SendGrpc") && msg.Contains("Info:")))
                        {
                            item.Category = "SendGrpc";
                            // 将 msg 内部的乱码文本解析成漂亮的 JSON
                            string parsedGrpc = ParseSendGrpcContent(msg);
                            
                            // 我们把解析好的 Grpc 内容拼接到原 JSON 显示里，或者替换 msg 字段
                            // 这里选择追加显示，方便对照
                            item.PrettyContent += "\n\n=== [SendGrpc Parsed Result] ===\n" + parsedGrpc;
                        }
                        else
                        {
                            // 提取常规 Category
                            int braceIndex = msg.IndexOf('{');
                            if (braceIndex > 0)
                            {
                                string prefix = msg.Substring(0, braceIndex).Trim();
                                prefix = prefix.Replace("[GRPC]", "").Replace(":", "").Trim();
                                if (!string.IsNullOrEmpty(prefix)) item.Category = prefix;
                                else item.Category = "JsonLog";
                            }
                            else
                            {
                                item.Category = "JsonLog";
                            }
                        }
                    }
                    catch { item.Category = "JsonRaw"; }
                }
                // Case 4: Error
                else if (content.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    item.Category = "Error";
                    item.PrettyContent = content;
                }
                else
                {
                    item.PrettyContent = content;
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

        // === 专门解析 SendGrpcMsg 的 key=val 格式 ===
        private static string ParseSendGrpcContent(string text)
        {
            try
            {
                // 1. 提取 Info 之后的内容
                int infoIndex = text.IndexOf("Info:");
                if (infoIndex == -1) return text;

                // 提取头部信息 (Info 之前的部分，比如 WarId, Pid)
                string headerPart = text.Substring(0, infoIndex).Trim();
                // 提取数据部分
                string dataPart = text.Substring(infoIndex + 5).Trim();

                JObject resultObj = new JObject();
                resultObj["_HeaderRaw"] = headerPart; // 保留头部原始文本
                
                JArray itemsArray = new JArray();
                
                // 按行分割
                var lines = dataPart.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    string trimLine = line.Trim();
                    // 只处理包含 Details 的行
                    if (trimLine.Contains("Details:"))
                    {
                        JObject itemObj = new JObject();
                        // 去掉前缀 "GoldDashWarItem Details:"
                        int detIdx = trimLine.IndexOf("Details:");
                        string kvStr = trimLine.Substring(detIdx + 8).Trim();
                        
                        // 按逗号分割 key=val (简单分割，假设 value 里没逗号)
                        var parts = kvStr.Split(',');
                        foreach (var part in parts)
                        {
                            var kv = part.Split('=');
                            if (kv.Length >= 2) // 兼容 value 为空的情况 (belong_uuid=)
                            {
                                string key = kv[0].Trim();
                                string val = string.Join("=", kv.Skip(1)).Trim(); // 防止 value 里也有等号
                                
                                // 尝试转数字
                                if (long.TryParse(val, out long numVal)) itemObj[key] = numVal;
                                else itemObj[key] = val;
                            }
                        }
                        itemsArray.Add(itemObj);
                    }
                }

                resultObj["Items"] = itemsArray;
                return resultObj.ToString(Formatting.Indented);
            }
            catch
            {
                return text; // 解析挂了就返回原文本
            }
        }

        private static string ExtractPid(string text)
        {
            var match = _pidRegex.Match(text);
            return match.Success ? match.Groups[1].Value : "";
        }

        private static string SmartFormatJson(string input)
        {
            try
            {
                var token = JToken.Parse(input);
                RecursivelyExpandJson(token);
                return token.ToString(Formatting.Indented);
            }
            catch { return input; }
        }

        private static void RecursivelyExpandJson(JToken token)
        {
            if (token.Type == JTokenType.Object)
            {
                foreach (var child in token.Children<JProperty>()) RecursivelyExpandJson(child.Value);
            }
            else if (token.Type == JTokenType.Array)
            {
                foreach (var child in token.Children()) RecursivelyExpandJson(child);
            }
            else if (token.Type == JTokenType.String)
            {
                string str = token.ToString();
                if (str.Contains("{") && str.Contains("}"))
                {
                    try 
                    {
                        var inner = JToken.Parse(str);
                        RecursivelyExpandJson(inner);
                        if (token.Parent is JProperty p) p.Value = inner;
                        return;
                    } catch { }

                    try 
                    {
                        int first = str.IndexOf('{');
                        int last = str.LastIndexOf('}');
                        if (first != -1 && last > first)
                        {
                            string prefix = str.Substring(0, first).Trim();
                            string jsonPart = str.Substring(first, last - first + 1);
                            var inner = JToken.Parse(jsonPart);
                            RecursivelyExpandJson(inner);

                            JObject wrapper = new JObject();
                            if (!string.IsNullOrEmpty(prefix)) wrapper["_desc"] = prefix;
                            wrapper["_data"] = inner;

                            if (token.Parent is JProperty p) p.Value = wrapper;
                        }
                    } catch { }
                }
            }
        }
    }
}