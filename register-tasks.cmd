@echo off
echo 正在注册钉钉打卡计划任务（wake 模式，工作日 07:50 / 17:40）...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-task.ps1" -Mode wake
echo.
if errorlevel 1 (
  echo 注册失败。请右键本文件，选择“以管理员身份运行”再试一次。
) else (
  echo 注册完成。可在“任务计划程序”里看到 DingTalkClockIn-Morning / DingTalkClockIn-Evening。
)
pause
