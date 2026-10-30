# VoxelPoolGodot — 体素池核（Godot 复刻版）

对 B 站 Toy「体素池核」（作者：烧风）的渲染技术思路做研究的独立复刻实现。
全部代码为原创，仅采用公开发表的经典算法：

- **Amanatides & Woo (1987)** 体素 DDA 遍历
- **Beer-Lambert** 定律（彩色玻璃/水体吸收着色）
- **Snell 折射 + Schlick 菲涅尔近似**
- **光子映射**（Jensen）的极简实时变体：太阳→水面折射→池底溅射焦散图
- **路径追踪**：余弦加权漫反射弹射 + 下一次事件估计（NEE）+ R2 低差异抖动
- **渐进式累积**：静止时跨帧收敛（类离线渲染器）

## 运行要求

- Godot 4.7+（.NET/C# 版）
- **Forward+ (Vulkan) 渲染器**（用了 RenderingDevice compute，Compatibility 下不可用）

## 渲染管线（每帧）

```
caustics.glsl    256×256 太阳光子 → 折射入水 → 池底定点数 atomicAdd 溅射
                       ↓（跨帧累积，场景修改时清零）
pathtrace.glsl   相机光线 → 体素 DDA（含水介质状态机）+ 解析球体
                 漫反射 GI(4 弹射) + 太阳/光球 NEE + 水下焦散采样
                 写入 rgba16f 累积缓冲
                       ↓
present.glsl     累积平均 → 曝光 → filmic tonemap → gamma → rgba8
```

## 操作

| 按键 | 功能 |
|---|---|
| 点击画面 | 捕获鼠标（进入观察模式） |
| ESC | 释放鼠标 |
| WASD / Space / Ctrl | 移动 / 上升 / 下降（Shift 加速） |
| 左键 / 右键 | 删除 / 放置体素（准星处） |
| T | 切换放置材质（含 7 色玻璃砖） |
| 1 / 2 | 泳池 / 康奈尔盒预设 |
| R | 重置相机 |

## 彩色玻璃体素

材质 ID 10–16 为彩色玻璃（红/绿/蓝/青/洋红/黄/白），走**统一介质状态机**：
每个玻璃/水界面用**精确介质菲涅尔方程**（s/p 偏振平均，TIR 返 1）做反射 vs 折射
随机分支 → 折射进入体内按 Beer-Lambert 吸收（σ 存在材质表 albedo.rgb）→
出射再折射。菲涅尔反射带来棱边亮线和天空镜面反光，水面也获得视角相关的天空反光。
预设里自带一面彩虹玻璃墙（池西侧）和一坨白玻璃方块（东南角）作展示。

## 结构

```
shaders/  pathtrace.glsl   路径追踪主 shader
          caustics.glsl    焦散光子映射
          caustics_clear.glsl / present.glsl
src/      PathTracer.cs    RenderingDevice 管线封装
          VoxelWorld.cs    体素数据 + 预设场景 + CPU DDA 拾取
          FlyCamera.cs     飞行相机
          Main.cs          主控
scenes/   main.tscn
```

## 与原作对应的技术点

| 原作（分析结论） | 本实现 |
|---|---|
| 片元着色器光线追踪 | compute shader 光线追踪 |
| 16×16×128 位打包体素 | 32³ 位打包体素（4 体素/u32） |
| 宏块跳空加速 | 暂未实现（32³ 很小，DDA 256 步上限足够） |
| 焦散光子映射（分帧分批） | 每帧 65K 光子、跨帧累积、定点数 r32ui 原子溅射 |
| 3D 纹理辐射传播 GI | 路径追踪多弹射 GI（更简单、更准但更贵） |
| Beer-Lambert 彩色玻璃球 | 同思路，吸收系数表按色系设定 |
| TAA + 直方图自动曝光 | 渐进收敛 + 固定曝光（tonemap 保留） |
