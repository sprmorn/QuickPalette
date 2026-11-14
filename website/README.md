# 轻启 QuickPalette 官网（静态站）

纯静态 HTML/CSS/JS，无构建步骤，可直接部署到 **Cloudflare Pages**（或任意静态托管）。

## 目录结构

```
website/
├── index.html              # 官网单页
├── assets/
│   ├── css/style.css       # 样式（腾讯电脑管家官网玻璃材质设计语言）
│   ├── js/main.js          # 交互（入场动画 / 回顶 / 平滑锚点）
│   └── img/
│       ├── logo.png        # 方标（导航/图标）
│       ├── product-logo.png# 产品横版 logo（首屏）
│       ├── brand.png       # 品牌 logo（页脚）
│       └── shots/          # 产品截图
└── README.md
```

## 部署到 Cloudflare Pages

### 方式一：拖拽上传（最快）
1. 打开 [Cloudflare Dashboard](https://dash.cloudflare.com/) → **Workers & Pages** → **Create** → **Pages** → **Upload assets**
2. 把 `website/` 目录**整体拖入**（保持目录结构）
3. 部署完成后得到 `https://<项目名>.pages.dev`

### 方式二：Git 集成（持续部署）
1. 把 `website/` 推送到 GitHub 仓库（可以是 QuickPalette 仓库的 `website/` 目录或独立仓库）
2. Cloudflare Pages → **Connect to Git** → 选择仓库
3. 构建设置：
   - **Framework preset**：None
   - **Build command**：（留空）
   - **Build output directory**：`website`（若 website 在仓库根目录则留空或填 `/`）
4. 以后推送即自动重新部署

## 本地预览

```powershell
cd website
python -m http.server 8080
# 浏览器打开 http://localhost:8080
```

## 维护说明

- 下载按钮指向 GitHub Releases latest（`https://github.com/sprmorn/QuickPalette/releases/latest`），
  发新版本无需改网页
- 版本号文案（如 `v1.8.11`）写在 `index.html` 的 `hero-note`，发版时手动更新
- 截图更新：替换 `assets/img/shots/` 同名文件即可
