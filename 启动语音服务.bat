@echo off
title DeepFish TTS (GPT-SoVITS v5turbo)
cd /d "D:\GPT-SoVITS-main\GPT-SoVITS-v2pro-20261006"
"D:\GPT-SoVITS-main\GPT-SoVITS-v2pro-20261006\runtime\python.exe" "D:\GPT-SoVITS-main\GPT-SoVITS-v2pro-20261006\api_v2.py" -a 127.0.0.1 -p 9880 -c "D:\GPT-SoVITS-main\GPT-SoVITS-v2pro-20261006\tts_infer_turbo.yaml"
pause