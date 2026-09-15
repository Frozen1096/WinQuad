@echo off
chcp 65001 >nul
rem ═══════════════════════════════════════════════════════════════
rem  WinQuad 配置编辑入口
rem  双击本文件即可打开配置目录，用记事本改完保存，
rem  然后在桌面四宫格上点右键 →「重新载入全部配置」，不必重启程序。
rem ═══════════════════════════════════════════════════════════════

echo.
echo 配置文件说明：
echo.
echo   config.json            总配置：尺寸 / 配色 / 字体 / 行为（所有宫格共享）
echo   group-xxx.json         每个四宫格的内容：格子的程序与外观，
echo                          以及它自己的占地和形状（layout 段）
echo.
echo 常用改动位置：
echo   group-xxx.json 的 items 数组，顺序固定为：
echo       items[0] = 左上    items[1] = 右上
echo       items[2] = 左下    items[3] = 右下
echo   每项三个关键字段：
echo       caption  格子里显示的字（格子窄，建议用短别名）
echo       path     要启动的文件，.lnk / .exe / .url 都行
echo       icon     图标来源，null = 自动用原程序图标（不会有快捷方式小箭头）
echo.
echo   注：json 里路径的反斜杠要写两个，例如 C:\\Users\\你的用户名\\Desktop\\xxx.lnk
echo.
echo 文字看不清？改 config.json 里 style 段的：
echo   plateAlpha    底板透明度，调大            （如 40 -^> 90）
echo   outlineAlpha  文字描边透明度，调大        （如 245 -^> 255）
echo   fontSizePt    字号，调大但可能要同时调小 iconSize
echo.
echo 想让某个四宫格占大一点、装更多程序？改它的 layout 段：
echo   footprintCols / footprintRows   占几个桌面图标格（每格 83x114 像素）
echo   cols / rows                     里面分几列几行
echo   两个都填 null 就是跟随总配置。这两层是独立的，所以可以
echo   「占 2x2 个图标格、里面放 3x3」。图形界面在管理器右栏「位置」里。
echo.
pause
start "" explorer "%~dp0"
