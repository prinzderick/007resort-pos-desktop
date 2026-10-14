@echo off
title SERI Resort POS - installer
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-POS.ps1" %*
