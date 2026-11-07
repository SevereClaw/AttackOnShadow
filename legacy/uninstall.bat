@echo off
rem Restores the original Attack Shark Driver files.
call "%~dp0patch\run.cmd" uninstall "%~1"
