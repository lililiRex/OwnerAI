namespace OwnerAI.Agent.Scheduler;

/// <summary>
/// 简化 Cron 表达式解析器 — 支持 5 段格式: "分 时 日 月 周"
/// <para>
/// 支持的语法:
/// - 具体值: 30 (第 30 分钟)
/// - 通配符: * (每个)
/// - 间隔: */5 (每 5 分钟)
/// - 范围: 9-17 (9 到 17)
/// - 列表: 1,3,5 (1、3、5)
/// </para>
/// <para>
/// 常见示例:
/// - "0 9 * * *" → 每天 9:00
/// - "*/30 * * * *" → 每 30 分钟
/// - "0 9 * * 1-5" → 工作日 9:00
/// - "0 0 1 * *" → 每月 1 日 0:00
/// - "0 9,18 * * *" → 每天 9:00 和 18:00
/// </para>
/// </summary>
public static class CronHelper
{
    /// <summary>
    /// 验证 Cron 表达式是否合法
    /// </summary>
    public static bool IsValid(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;

        return IsValidField(parts[0], 0, 59)  // 分
            && IsValidField(parts[1], 0, 23)   // 时
            && IsValidField(parts[2], 1, 31)   // 日
            && IsValidField(parts[3], 1, 12)   // 月
            && IsValidField(parts[4], 0, 6);   // 周 (0=Sun)
    }

    /// <summary>
    /// 计算给定时间之后的下一次触发时间
    /// </summary>
    public static DateTimeOffset? GetNextOccurrence(string expression, DateTimeOffset after)
    {
        if (!IsValid(expression)) return null;

        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var minutes = ParseField(parts[0], 0, 59);
        var hours = ParseField(parts[1], 0, 23);
        var daysOfMonth = ParseField(parts[2], 1, 31);
        var months = ParseField(parts[3], 1, 12);
        var daysOfWeek = ParseField(parts[4], 0, 6);

        // 从 after 的下一分钟开始搜索，最多搜索 2 年
        var candidate = after.AddMinutes(1);
        candidate = new DateTimeOffset(
            candidate.Year, candidate.Month, candidate.Day,
            candidate.Hour, candidate.Minute, 0, candidate.Offset);

        var maxDate = after.AddYears(2);

        while (candidate < maxDate)
        {
            if (months.Contains(candidate.Month)
                && daysOfMonth.Contains(candidate.Day)
                && daysOfWeek.Contains((int)candidate.DayOfWeek)
                && hours.Contains(candidate.Hour)
                && minutes.Contains(candidate.Minute))
            {
                return candidate;
            }

            candidate = candidate.AddMinutes(1);

            // 优化: 如果月份不匹配，跳到下个月
            if (!months.Contains(candidate.Month))
            {
                candidate = SkipToNextMonth(candidate, months);
                continue;
            }

            // 优化: 如果日期不匹配，跳到下一天
            if (!daysOfMonth.Contains(candidate.Day) || !daysOfWeek.Contains((int)candidate.DayOfWeek))
            {
                candidate = candidate.Date.AddDays(1).ToDateTimeOffset(candidate.Offset);
                continue;
            }

            // 优化: 如果小时不匹配，跳到下一个匹配小时
            if (!hours.Contains(candidate.Hour))
            {
                candidate = SkipToNextHour(candidate, hours);
                continue;
            }
        }

        return null;
    }

    /// <summary>
    /// 获取 Cron 表达式的人类可读描述
    /// </summary>
    public static string Describe(string? expression)
    {
        if (!IsValid(expression)) return "无效的 Cron 表达式";

        var parts = expression!.Trim().Split(' ');

        var minute = parts[0];
        var hour = parts[1];
        var dayOfMonth = parts[2];
        var month = parts[3];
        var dayOfWeek = parts[4];

        var desc = new System.Text.StringBuilder();

        // 分钟
        if (minute.StartsWith("*/", StringComparison.Ordinal))
            desc.Append($"每 {minute[2..]} 分钟");
        else if (minute == "*")
            desc.Append("每分钟");
        else
            desc.Append($"第 {minute} 分");

        // 小时
        if (hour.StartsWith("*/", StringComparison.Ordinal))
            desc.Append($", 每 {hour[2..]} 小时");
        else if (hour != "*")
            desc.Append($", {hour} 点");

        // 日
        if (dayOfMonth.StartsWith("*/", StringComparison.Ordinal))
            desc.Append($", 每 {dayOfMonth[2..]} 天");
        else if (dayOfMonth != "*")
            desc.Append($", {dayOfMonth} 日");

        // 月
        if (month != "*")
            desc.Append($", {month} 月");

        // 周
        if (dayOfWeek != "*")
        {
            var weekNames = dayOfWeek.Replace("0", "日").Replace("1", "一").Replace("2", "二")
                .Replace("3", "三").Replace("4", "四").Replace("5", "五").Replace("6", "六");

            if (dayOfWeek == "1-5")
                desc.Append(", 工作日");
            else
                desc.Append($", 周{weekNames}");
        }

        return desc.ToString();
    }

    private static HashSet<int> ParseField(string field, int min, int max)
    {
        var result = new HashSet<int>();

        foreach (var part in field.Split(','))
        {
            if (part == "*")
            {
                for (var i = min; i <= max; i++) result.Add(i);
            }
            else if (part.StartsWith("*/", StringComparison.Ordinal) && int.TryParse(part.AsSpan(2), out var step))
            {
                for (var i = min; i <= max; i += step) result.Add(i);
            }
            else if (part.Contains('-'))
            {
                var range = part.Split('-');
                if (int.TryParse(range[0], out var from) && int.TryParse(range[1], out var to))
                {
                    for (var i = from; i <= to && i <= max; i++) result.Add(i);
                }
            }
            else if (int.TryParse(part, out var val))
            {
                result.Add(val);
            }
        }

        return result;
    }

    private static bool IsValidField(string field, int min, int max)
    {
        foreach (var part in field.Split(','))
        {
            if (part == "*") continue;
            if (part.StartsWith("*/", StringComparison.Ordinal))
            {
                if (!int.TryParse(part.AsSpan(2), out var step) || step <= 0 || step > max) return false;
                continue;
            }
            if (part.Contains('-'))
            {
                var range = part.Split('-');
                if (range.Length != 2) return false;
                if (!int.TryParse(range[0], out var from) || !int.TryParse(range[1], out var to)) return false;
                if (from < min || to > max || from > to) return false;
                continue;
            }
            if (!int.TryParse(part, out var val) || val < min || val > max) return false;
        }
        return true;
    }

    private static DateTimeOffset SkipToNextMonth(DateTimeOffset dt, HashSet<int> validMonths)
    {
        var current = dt;
        for (var i = 0; i < 24; i++) // max 2 years
        {
            var nextMonth = current.Month + 1;
            var nextYear = current.Year;
            if (nextMonth > 12) { nextMonth = 1; nextYear++; }

            current = new DateTimeOffset(nextYear, nextMonth, 1, 0, 0, 0, current.Offset);
            if (validMonths.Contains(current.Month))
                return current;
        }
        return dt.AddYears(2);
    }

    private static DateTimeOffset SkipToNextHour(DateTimeOffset dt, HashSet<int> validHours)
    {
        var next = new DateTimeOffset(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, dt.Offset).AddHours(1);
        for (var i = 0; i < 24; i++)
        {
            if (validHours.Contains(next.Hour))
                return next;
            next = next.AddHours(1);
        }
        return next;
    }

    private static DateTimeOffset ToDateTimeOffset(this DateTime dt, TimeSpan offset)
        => new(dt, offset);
}
