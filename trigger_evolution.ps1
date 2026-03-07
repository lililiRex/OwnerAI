# 手动触发 OwnerAI 进化检查
# 此脚本通过向运行中的 OwnerAI 进程发送信号来触发进化检查

Write-Host "🧬 手动触发 OwnerAI 进化检查..." -ForegroundColor Cyan

# 方法 1: 尝试通过命名管道或本地 API 触发
# 由于 OwnerAI 正在运行，我们创建一个标记文件来通知进化服务

$triggerFile = "$env:TEMP\OwnerAI_Evolution_Trigger.txt"
$gapId = "dca05a29013d"  # 最高优先级缺口：角色一致性控制

$content = @"
{
  "Action": "TriggerEvolution",
  "GapId": "$gapId",
  "Priority": 5,
  "Timestamp": "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
  "Source": "manual_trigger"
}
"@

$content | Out-File -FilePath $triggerFile -Encoding UTF8

Write-Host "✅ 触发文件已创建：$triggerFile" -ForegroundColor Green
Write-Host "📝 内容:" -ForegroundColor Yellow
Write-Host $content

Write-Host ""
Write-Host "⏳ OwnerAI 进化后台服务将在下一个检查周期检测到此文件并执行进化任务..." -ForegroundColor Cyan
Write-Host "   或者等待 10 分钟后的自动调度周期。" -ForegroundColor Gray
