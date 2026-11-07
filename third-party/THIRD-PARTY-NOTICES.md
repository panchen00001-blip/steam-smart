# 第三方组件与规则来源

## Node.js

Windows 发布版 EXE 内置 Node.js v24.22.1，用于运行本仓库的 JavaScript 引擎。

- 项目：https://nodejs.org/
- 对应发行版完整授权：https://github.com/nodejs/node/blob/v24.22.1/LICENSE
- 授权副本：[NODE-LICENSE.txt](NODE-LICENSE.txt)

完整授权包含 Node.js 本身及其内置第三方组件的相关声明。安装包会同时附带授权文件。

## 默认 CDN 规则

`engine/smart-config.defaults.json` 中的默认 Steam 下载来源整理自以下公开规则源：

https://www.dogfight360.com/Usbeam/dl2v6_v2.ini

规则中的域名与地址属于对应服务提供方，可能随时调整。这里的来源标注不表示得到其官方授权，也不授予对第三方程序或商标的权利。本仓库不分发 UsbEAm 程序文件。

## Windows 组件

应用使用系统提供的 .NET Framework / WPF、Windows Forms、PowerShell、UI Automation 与 DWM API；不在源码仓库中分发这些系统组件。
