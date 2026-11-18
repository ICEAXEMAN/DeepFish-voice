@echo off
setlocal
title DeepFish TTS (GPT-SoVITS v5turbo)

rem ------------------------------------------------------------------
rem Set GPT to YOUR GPT-SoVITS folder: the one containing api_v2.py
rem and runtime\python.exe. Keep the quotes.
rem ------------------------------------------------------------------
set "GPT=D:\GPT-SoVITS-main\GPT-SoVITS-v2pro-20261006"

if not exist "%GPT%\api_v2.py" (
  echo [DeepFish] api_v2.py not found under:
  echo    %GPT%
  echo Open this .bat in Notepad and change the GPT= line to your own GPT-SoVITS folder.
  pause
  exit /b 1
)

cd /d "%GPT%"
"%GPT%\runtime\python.exe" "%GPT%\api_v2.py" -a 127.0.0.1 -p 9880 -c "%GPT%\tts_infer_turbo.yaml"
pause