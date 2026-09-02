// ════════════════════════════════════════════════════════════════════════
// 归巢两步地图选择器（NestMapPicker）
// 用途：自动化任务「归巢模式」的统一入口 —— 一张地图分两步选车队 + 选巢区：
//   Step1 选车队：站点 = 淡色背景（不可选）；车辆按状态着色，仅就绪可框选，
//                 悬停任意车显示信息（车名/状态/位置/电量）。
//   Step2 选巢区：车辆 = 淡色背景（不可选）；站点按类型着色可框选（楼层切换，
//                 启用可选/禁用灰，既有巢区 Mark 高亮保留）。
//   总结：确认前弹出摘要（车队 N 台 + 巢区 M 个站点）→ 确认返回
//         {Vehicles:[...], Marks:[...]} 的 JSON；取消/Esc/遮罩 → null。
// 交互约定：左键拖拽=框选(替换)；Shift/Ctrl 拖拽=增选/减选；单击=切换；
// 右键/中键拖拽=平移；滚轮=缩放；Esc/遮罩点击=取消。
// 坐标系：站点与车辆一致（毫米，笛卡尔 Y 向上）。
// ════════════════════════════════════════════════════════════════════════
(function () {
    'use strict';

    // 类型位 → 主色/图例名（与 C# MapStationTypeBits 一致；优先顺序决定主色）
    const TYPE_PRIORITY = [
        { bit: 64,   name: '分拣点',       color: '#fb923c' },
        { bit: 128,  name: '人工拣选台',   color: '#f472b6' },
        { bit: 8,    name: '接驳位',       color: '#4ade80' },
        { bit: 4,    name: '储位',         color: '#38bdf8' },
        { bit: 32,   name: '充电点',       color: '#fbbf24' },
        { bit: 16,   name: '停车位',       color: '#a78bfa' },
        { bit: 256,  name: '电梯点',       color: '#2dd4bf' },
        { bit: 1,    name: '普通道路',     color: '#64748b' },
        { bit: 2,    name: '高速路',       color: '#94a3b8' },
        { bit: 512,  name: '其他',         color: '#cbd5e1' }
    ];

    function primaryType(type) {
        for (const t of TYPE_PRIORITY) if ((type & t.bit) !== 0) return t;
        return { bit: 0, name: '未配置', color: '#94a3b8' };
    }

    function decodeTypeNames(type) {
        const names = [];
        for (const t of TYPE_PRIORITY) if ((type & t.bit) !== 0) names.push(t.name);
        return names.length ? names : ['未配置(' + type + ')'];
    }

    function vehicleStyle(v) {
        if (!v.IsReady) {
            if (!v.IsOnline) return { color: '#64748b', name: '离线' };
            if ((v.ExecutionState || '').toUpperCase() === 'ERROR') return { color: '#f87171', name: '报错' };
            if (v.IsCharging) return { color: '#f59e0b', name: '充电中' };
            return { color: '#fbbf24', name: '执行中' };
        }
        return { color: '#4ade80', name: '就绪' };
    }

    function makeEl(tag, cls, text) {
        const el = document.createElement(tag);
        if (cls) el.className = cls;
        if (text !== undefined && text !== null) el.textContent = text;
        return el;
    }

    let S = null;

    /**
     * 打开归巢两步地图选择器（先选车队，再选巢区，最后总结确认）。
     * @param {string} configJson 的 JSON：
     *   { Stations:[{Mark,StationType,X,Y,Floor,StaEnable}],
     *     Vehicles:[{Name,X,Y,IsReady,ExecutionState,UtilizationState,Power,Location,IsCharging}],
     *     PreselectedVehicles:string[], PreselectedMarks:string[] }
     * @returns {Promise<string|null>} 确认→{"Vehicles":[...],"Marks":[...]} JSON；取消→null。
     */
    window.grcsNestPickerOpen = function (configJson) {
        return new Promise(function (resolve) {
            let config;
            try { config = JSON.parse(configJson); } catch (e) { config = null; }
            if (!config || !Array.isArray(config.Stations) || config.Stations.length === 0
                || !Array.isArray(config.Vehicles) || config.Vehicles.length === 0) {
                resolve(null);
                return;
            }
            if (S) {
                try { S.resolve(null); } catch (e) { }
                try { S.overlay.remove(); } catch (e) { }
            }
            buildPicker(config, resolve);
        });
    };

    function buildPicker(config, resolve) {
        // ── DOM 骨架 ──
        const overlay = makeEl('div', 'smp-overlay');
        const modal = makeEl('div', 'smp-modal');

        const toolbar = makeEl('div', 'smp-toolbar');
        const titleEl = makeEl('div', 'smp-title', '');
        toolbar.appendChild(titleEl);
        const stepEl = makeEl('span', 'smp-count', '');
        toolbar.appendChild(stepEl);
        const countEl = makeEl('span', 'smp-count', '');
        toolbar.appendChild(countEl);
        const btnClear = makeEl('button', 'btn btn-outline btn-sm', '🗑 清空');
        const btnAll = makeEl('button', 'btn btn-outline btn-sm', '');
        const btnBack = makeEl('button', 'btn btn-outline btn-sm', '⬅️ 返回选车');
        const btnNext = makeEl('button', 'btn btn-primary btn-sm', '➡️ 去选巢区');
        const btnFinish = makeEl('button', 'btn btn-primary btn-sm', '✅ 完成');
        btnClear.type = btnAll.type = btnBack.type = btnNext.type = btnFinish.type = 'button';
        btnBack.style.display = 'none';
        btnFinish.style.display = 'none';
        toolbar.appendChild(btnClear);
        toolbar.appendChild(btnAll);
        toolbar.appendChild(btnBack);
        toolbar.appendChild(btnNext);
        toolbar.appendChild(btnFinish);
        modal.appendChild(toolbar);

        const wrap = makeEl('div', 'smp-canvas-wrap');
        const canvas = makeEl('canvas', 'smp-canvas');
        wrap.appendChild(canvas);
        const tooltip = makeEl('div', 'smp-tooltip');
        tooltip.style.display = 'none';
        wrap.appendChild(tooltip);
        const summary = makeEl('div', 'smp-summary');
        summary.style.display = 'none';
        wrap.appendChild(summary);
        modal.appendChild(wrap);

        const legend = makeEl('div', 'smp-legend');
        modal.appendChild(legend);
        const hint = makeEl('div', 'smp-hint', '');
        modal.appendChild(hint);

        overlay.appendChild(modal);
        document.body.appendChild(overlay);

        const ctx = canvas.getContext('2d');

        // ── 数据 ──
        const stations = config.Stations;
        const vehicles = config.Vehicles;
        const floors = stations.map(function (s) { return s.Floor; }).filter(function (f, i, a) { return a.indexOf(f) === i; }).sort(function (a, b) { return a - b; });
        const selectionV = new Set((config.PreselectedVehicles || []).map(String).filter(Boolean));
        const selectionS = new Set((config.PreselectedMarks || []).map(String).filter(Boolean));
        const stationMarkSet = new Set(stations.map(function (s) { return s.Mark; }));
        // 被车辆占用的站点：location 命中站点 Mark（任意车辆，含离线/执行中）→ Step2 不可选
        const occupiedKeys = new Set();
        const occupiedVehicleNames = new Map();
        for (const v of vehicles) {
            const loc = String(v.Location || '').trim().toLowerCase();
            if (!loc) continue;
            occupiedKeys.add(loc);
            if (!occupiedVehicleNames.has(loc)) occupiedVehicleNames.set(loc, v.Name);
        }
        function isOccupied(mark) { return occupiedKeys.has(String(mark).trim().toLowerCase()); }
        // 巢区候选集外的既有 Mark（禁用/类型外）：替换框选时保留，避免丢数据
        const preservedS = new Set(Array.from(selectionS).filter(function (m) { return !stationMarkSet.has(m); }));

        // ── 步骤状态 ──
        let step = 1; // 1=选车队 2=选巢区 3=总结
        let currentFloor = floors.length ? floors[0] : 0;
        let view = { scale: 1, ox: 0, oy: 0, fitScale: 1 };
        let drag = null;
        let hover = null;

        function currentPoints() {
            if (step === 1) {
                const ready = [], other = [];
                for (const v of vehicles) (v.IsReady ? ready : other).push(v);
                return { all: vehicles, enabled: ready, disabled: other, isVehicle: true };
            }
            const enabled = [], occupied = [], disabled = [];
            for (const s of stations) {
                if (!s.StaEnable || s.Floor !== currentFloor) { disabled.push(s); continue; }
                if (isOccupied(s.Mark)) occupied.push(s); else enabled.push(s);
            }
            return { all: enabled.concat(occupied).concat(disabled), enabled: enabled, occupied: occupied, disabled: disabled, isVehicle: false };
        }

        function fit() {
            const w = canvas.clientWidth, h = canvas.clientHeight;
            const pts = currentPoints().all;
            if (pts.length === 0) {
                view = { scale: 1, ox: 0, oy: 0, fitScale: 1 };
                draw();
                return;
            }
            if (w === 0 || h === 0) { requestAnimationFrame(fit); return; }
            let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
            for (const p of pts) {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            }
            const spanX = maxX - minX, spanY = maxY - minY;
            const pad = 56;
            let scale = Math.min((w - pad * 2) / (spanX || 1), (h - pad * 2) / (spanY || 1));
            if (!isFinite(scale) || scale <= 0) scale = 1;
            if (spanX === 0 && spanY === 0) scale = Math.max(w, h) / 16;
            const cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            view = { scale: scale, ox: w / 2 - cx * scale, oy: h / 2 + cy * scale, fitScale: scale };
            draw();
        }

        function draw() {
            const w = canvas.clientWidth, h = canvas.clientHeight;
            if (w === 0 || h === 0) return;
            const dpr = window.devicePixelRatio || 1;
            canvas.width = w * dpr;
            canvas.height = h * dpr;
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            ctx.clearRect(0, 0, w, h);

            const { scale, ox, oy } = view;
            drawGrid(w, h);

            // 背景：另一类元素淡色小点
            if (step === 1) {
                for (const s of stations) {
                    const sx = s.X * scale + ox, sy = oy - s.Y * scale;
                    if (sx < -20 || sx > w + 20 || sy < -20 || sy > h + 20) continue;
                    ctx.beginPath();
                    ctx.arc(sx, sy, 2.4, 0, Math.PI * 2);
                    ctx.fillStyle = 'rgba(100,116,139,0.55)';
                    ctx.fill();
                }
            } else {
                // 车辆背景：绘制在站点之上、占用标识之下（半透明状态色，停在站点上的车也能透出）
                for (const v of vehicles) {
                    const sx = v.X * scale + ox, sy = oy - v.Y * scale;
                    if (sx < -30 || sx > w + 30 || sy < -30 || sy > h + 30) continue;
                    ctx.globalAlpha = 0.65;
                    ctx.beginPath();
                    ctx.arc(sx, sy, 4, 0, Math.PI * 2);
                    ctx.fillStyle = vehicleStyle(v).color;
                    ctx.fill();
                    ctx.globalAlpha = 1;
                }
            }

            const inBox = drag && drag.mode === 'box' && drag.moved;

            if (step === 1) {
                // 车辆：就绪可选，其余按状态着色
                for (const v of vehicles) {
                    const sx = v.X * scale + ox, sy = oy - v.Y * scale;
                    if (sx < -40 || sx > w + 40 || sy < -40 || sy > h + 40) continue;
                    const st = vehicleStyle(v);
                    if (!v.IsReady) {
                        drawPoint(sx, sy, false, st.color, 6.5, 1, 'rgba(255,255,255,0.25)', null);
                        continue;
                    }
                    const isSel = selectionV.has(v.Name);
                    const isHit = inBox ? rectContains(drag, sx, sy) : false;
                    const isHover = hover !== null && hover.Name === v.Name;
                    if (isSel) drawPoint(sx, sy, true, '#4ade80', 11, 3, '#ffffff', v.Name);
                    else if (isHit) drawPoint(sx, sy, true, '#4ade80', 9.5, 2, 'rgba(255,255,255,0.85)', v.Name);
                    else if (isHover) drawPoint(sx, sy, false, '#4ade80', 7, 2, '#ffffff', null);
                    else drawPoint(sx, sy, false, '#4ade80', 7, 1.6, 'rgba(255,255,255,0.7)', null);
                }
            } else {
                // 站点：类型着色可选 / 被占用红禁不可选 / 禁用置灰
                const pts = currentPoints();
                for (const p of pts.disabled) {
                    const sx = p.X * scale + ox, sy = oy - p.Y * scale;
                    if (sx < -30 || sx > w + 30 || sy < -30 || sy > h + 30) continue;
                    drawPoint(sx, sy, false, '#475569', 3.5, 0, null, null);
                }
                for (const p of pts.enabled) {
                    const sx = p.X * scale + ox, sy = oy - p.Y * scale;
                    if (sx < -30 || sx > w + 30 || sy < -30 || sy > h + 30) continue;
                    const isSel = selectionS.has(p.Mark);
                    const isHit = inBox ? rectContains(drag, sx, sy) : false;
                    const isHover = hover !== null && hover.Mark === p.Mark;
                    const col = primaryType(p.StationType).color;
                    if (isSel) drawPoint(sx, sy, true, col, 8.5, 3, '#ffffff', p.Mark);
                    else if (isHit) drawPoint(sx, sy, true, col, 7, 2, 'rgba(255,255,255,0.85)', p.Mark);
                    else if (isHover) drawPoint(sx, sy, false, col, 5.5, 2, '#ffffff', null);
                    else drawPoint(sx, sy, false, col, 4.5, 1.6, 'rgba(255,255,255,0.7)', null);
                }
                // 被占用点：红禁标识（最上层，明确不可选）
                for (const p of pts.occupied) {
                    const sx = p.X * scale + ox, sy = oy - p.Y * scale;
                    if (sx < -40 || sx > w + 40 || sy < -40 || sy > h + 40) continue;
                    const isHover = hover !== null && hover.Mark === p.Mark;
                    drawOccupied(sx, sy, p.Mark, isHover);
                }
            }

            if (inBox) {
                const x0 = Math.min(drag.x0, drag.x1), x1 = Math.max(drag.x0, drag.x1);
                const y0 = Math.min(drag.y0, drag.y1), y1 = Math.max(drag.y0, drag.y1);
                ctx.fillStyle = 'rgba(56,189,248,0.10)';
                ctx.fillRect(x0, y0, x1 - x0, y1 - y0);
                ctx.strokeStyle = '#38bdf8';
                ctx.lineWidth = 1;
                ctx.setLineDash([5, 4]);
                ctx.strokeRect(x0, y0, x1 - x0, y1 - y0);
                ctx.setLineDash([]);
            }
        }

        function drawPoint(sx, sy, highlight, color, radius, ringWidth, ringColor, label) {
            if (highlight) {
                ctx.beginPath();
                ctx.arc(sx, sy, radius + 4, 0, Math.PI * 2);
                ctx.fillStyle = 'rgba(103,232,249,0.25)';
                ctx.fill();
            }
            ctx.beginPath();
            ctx.arc(sx, sy, radius, 0, Math.PI * 2);
            ctx.fillStyle = color;
            ctx.fill();
            if (ringWidth > 0) {
                ctx.lineWidth = ringWidth;
                ctx.strokeStyle = ringColor || 'rgba(255,255,255,0.6)';
                ctx.stroke();
            }
            if (label) {
                ctx.fillStyle = 'rgba(226,232,240,0.95)';
                ctx.font = '10px Consolas, monospace';
                ctx.textAlign = 'left';
                ctx.fillText(label, sx + radius + 5, sy + 3);
            }
        }

        /// 被占用点：深红圆点 + 红色圆环 + 交叉斜线（禁止符），hover 时加亮圈。
        function drawOccupied(sx, sy, mark, isHover) {
            if (isHover) {
                ctx.beginPath();
                ctx.arc(sx, sy, 10, 0, Math.PI * 2);
                ctx.fillStyle = 'rgba(248,113,113,0.18)';
                ctx.fill();
            }
            ctx.beginPath();
            ctx.arc(sx, sy, 6, 0, Math.PI * 2);
            ctx.fillStyle = 'rgba(127,29,29,0.9)';
            ctx.fill();
            ctx.lineWidth = 2;
            ctx.strokeStyle = '#f87171';
            ctx.stroke();
            ctx.beginPath();
            ctx.moveTo(sx - 4.2, sy - 4.2);
            ctx.lineTo(sx + 4.2, sy + 4.2);
            ctx.moveTo(sx + 4.2, sy - 4.2);
            ctx.lineTo(sx - 4.2, sy + 4.2);
            ctx.lineWidth = 2;
            ctx.strokeStyle = '#f87171';
            ctx.stroke();
            ctx.fillStyle = 'rgba(248,113,113,0.92)';
            ctx.font = '10px Consolas, monospace';
            ctx.textAlign = 'left';
            ctx.fillText(mark, sx + 8, sy + 3);
        }

        function drawGrid(w, h) {
            const { scale, ox, oy } = view;
            const step = niceStep(90 / scale);
            if (!isFinite(step) || step <= 0) return;
            ctx.strokeStyle = 'rgba(51,65,85,0.4)';
            ctx.lineWidth = 1;
            ctx.beginPath();
            const xStart = Math.floor((-ox) / step);
            for (let i = xStart; i * step * scale + ox < w; i++) {
                const sx = Math.round(i * step * scale + ox);
                ctx.moveTo(sx, 0);
                ctx.lineTo(sx, h);
            }
            const yStart = Math.ceil((oy - h) / step);
            for (let j = yStart; j * step * scale <= oy; j++) {
                const sy = Math.round(oy - j * step * scale);
                ctx.moveTo(0, sy);
                ctx.lineTo(w, sy);
            }
            ctx.stroke();
        }

        function niceStep(rough) {
            if (!isFinite(rough) || rough <= 0) return 1;
            const pow = Math.pow(10, Math.floor(Math.log10(rough)));
            const norm = rough / pow;
            let nice;
            if (norm < 1.5) nice = 1; else if (norm < 3.5) nice = 2; else if (norm < 7.5) nice = 5; else nice = 10;
            return nice * pow;
        }

        function rectContains(d, sx, sy) {
            const x0 = Math.min(d.x0, d.x1), x1 = Math.max(d.x0, d.x1);
            const y0 = Math.min(d.y0, d.y1), y1 = Math.max(d.y0, d.y1);
            return sx >= x0 && sx <= x1 && sy >= y0 && sy <= y1;
        }

        function hitTest(px, py) {
            const { scale, ox, oy } = view;
            let best = null, bestD = Infinity;
            if (step === 1) {
                for (const v of vehicles) {
                    const sx = v.X * scale + ox, sy = oy - v.Y * scale;
                    const d = Math.hypot(sx - px, sy - py);
                    if (d < bestD) { bestD = d; best = v; }
                }
            } else {
                for (const p of currentPoints().all) {
                    const sx = p.X * scale + ox, sy = oy - p.Y * scale;
                    const d = Math.hypot(sx - px, sy - py);
                    if (d < bestD) { bestD = d; best = p; }
                }
            }
            return bestD <= 10 ? best : null;
        }

        // ── 选中操作 ──
        function applyBox(x0, y0, x1, y1, additive) {
            const ax = Math.min(x0, x1), bx = Math.max(x0, x1);
            const ay = Math.min(y0, y1), by = Math.max(y0, y1);
            const { scale, ox, oy } = view;
            if (step === 1) {
                const hits = vehicles.filter(function (v) {
                    if (!v.IsReady) return false;
                    const sx = v.X * scale + ox, sy = oy - v.Y * scale;
                    return sx >= ax && sx <= bx && sy >= ay && sy <= by;
                }).map(function (v) { return v.Name; });
                if (additive) { for (const n of hits) toggleIn(selectionV, n); }
                else { selectionV.clear(); for (const n of hits) selectionV.add(n); }
            } else {
                const hits = currentPoints().enabled.filter(function (p) {
                    const sx = p.X * scale + ox, sy = oy - p.Y * scale;
                    return sx >= ax && sx <= bx && sy >= ay && sy <= by;
                }).map(function (p) { return p.Mark; });
                if (additive) { for (const m of hits) toggleIn(selectionS, m); }
                else {
                    selectionS.clear();
                    for (const m of hits) selectionS.add(m);
                    for (const m of preservedS) selectionS.add(m);
                }
            }
        }

        function toggleIn(set, key) {
            if (set.has(key)) set.delete(key); else set.add(key);
        }

        // ── 工具栏 / 步骤 ──
        function updateUI() {
            if (step === 1) {
                titleEl.textContent = '① 选车队（站点为背景，仅绿色就绪车可选）';
                stepEl.textContent = '步骤 1/2';
                countEl.textContent = '已选 ' + selectionV.size + ' 台车';
                btnAll.textContent = '☑ 全选就绪车';
                btnBack.style.display = 'none';
                btnNext.style.display = '';
                btnFinish.style.display = 'none';
            } else if (step === 2) {
                titleEl.textContent = '② 选巢区（车辆为背景，站点可选）';
                stepEl.textContent = '步骤 2/2';
                countEl.textContent = '巢区 ' + selectionS.size + ' 个站点';
                btnAll.textContent = '☑ 全选本层';
                btnBack.style.display = '';
                btnNext.style.display = 'none';
                btnFinish.style.display = '';
            }
            buildLegend();
            hint.textContent = step === 1
                ? '左键拖拽=框选(替换) · Shift/Ctrl 拖拽=增选/减选 · 单击车辆=切换 · 右键平移 · 滚轮缩放 · 悬停任意车可见信息'
                : '左键拖拽=框选(替换) · Shift/Ctrl 拖拽=增选/减选 · 单击站点=切换 · 右键平移 · 滚轮缩放 · 红叉点为被车辆占用不可选';
            if (step === 2 && floors.length > 1) {
                // 楼层切换 chips
                const old = toolbar.querySelector('.smp-floors');
                if (old) toolbar.removeChild(old);
                const floorsWrap = makeEl('div', 'smp-floors');
                for (const f of floors) {
                    const b = makeEl('button', 'chip-btn' + (f === currentFloor ? ' on' : ''), f + ' 层');
                    b.type = 'button';
                    b.addEventListener('click', function () { currentFloor = f; updateUI(); fit(); });
                    floorsWrap.appendChild(b);
                }
                toolbar.insertBefore(floorsWrap, btnClear);
            } else {
                const old = toolbar.querySelector('.smp-floors');
                if (old) toolbar.removeChild(old);
            }
        }

        function buildLegend() {
            legend.textContent = '';
            if (step === 1) {
                const defs = [
                    { name: '就绪', color: '#4ade80' },
                    { name: '充电中', color: '#f59e0b' },
                    { name: '执行中', color: '#fbbf24' },
                    { name: '报错', color: '#f87171' },
                    { name: '离线', color: '#64748b' }
                ];
                for (const d of defs) {
                    const item = makeEl('div', 'smp-legend-item');
                    const dot = makeEl('span', 'smp-legend-dot');
                    dot.style.background = d.color;
                    item.appendChild(dot);
                    item.appendChild(document.createTextNode(d.name));
                    legend.appendChild(item);
                }
            } else {
                const types = new Map();
                for (const p of stations) {
                    if (p.StaEnable && p.Floor === currentFloor) {
                        const t = primaryType(p.StationType);
                        if (!types.has(t.name)) types.set(t.name, t.color);
                    }
                }
                for (const entry of types) {
                    const item = makeEl('div', 'smp-legend-item');
                    const dot = makeEl('span', 'smp-legend-dot');
                    dot.style.background = entry[1];
                    item.appendChild(dot);
                    item.appendChild(document.createTextNode(entry[0]));
                    legend.appendChild(item);
                }
                const occ = makeEl('div', 'smp-legend-item');
                const occDot = makeEl('span', 'smp-legend-dot');
                occDot.style.background = '#f87171';
                occ.appendChild(occDot);
                occ.appendChild(document.createTextNode('🚫 被占用（不可选）'));
                legend.appendChild(occ);
            }
        }

        // ── 总结弹窗 ──
        function makeChips(items, emptyText) {
            const box = makeEl('div', 'smp-sum-chips');
            if (items.length === 0) {
                box.appendChild(makeEl('div', 'smp-sum-empty', emptyText));
                return box;
            }
            const shown = items.slice(0, 10);
            for (const it of shown) box.appendChild(makeEl('span', 'smp-sum-chip', it));
            if (items.length > 10) box.appendChild(makeEl('span', 'smp-sum-chip', '等 ' + items.length + ' 项'));
            return box;
        }

        function showSummary() {
            step = 3;
            canvas.style.display = 'none';
            tooltip.style.display = 'none';
            summary.style.display = 'flex';
            summary.textContent = '';

            const head = makeEl('div', 'smp-sum-head');
            head.appendChild(makeEl('div', 'smp-sum-title', '📋 归巢选择确认'));
            head.appendChild(makeEl('div', 'smp-sum-sub', '确认后车队与巢区立即保存生效'));
            summary.appendChild(head);

            const vNames = Array.from(selectionV).sort();
            const mNames = Array.from(selectionS).sort();
            // 车多：只保留前 M 台（M = 巢区点数，按选择顺序）；巢区为空时不截断
            const excess = selectionS.size > 0 && selectionV.size > selectionS.size;
            const limit = excess ? selectionS.size : selectionV.size;

            const vCard = makeEl('div', 'smp-sum-card fleet');
            const vHead = makeEl('div', 'smp-sum-card-head');
            vHead.appendChild(makeEl('span', 'smp-sum-card-name', '🚗 车队'));
            vHead.appendChild(makeEl('span', 'smp-sum-count', (excess ? limit + '/' : '') + selectionV.size + ' 台'));
            vCard.appendChild(vHead);
            vCard.appendChild(makeChips(vNames, '未选择，执行时自动捕获当前就绪车'));
            if (excess) {
                const warn = makeEl('div', 'smp-sum-warn', '⚠️ 车队 ' + selectionV.size + ' 台 > 巢区 ' + selectionS.size + ' 个点：仅前 ' + limit + ' 台参与归巢，其余自动移出车队');
                vCard.appendChild(warn);
            }
            summary.appendChild(vCard);

            const mCard = makeEl('div', 'smp-sum-card nest');
            const mHead = makeEl('div', 'smp-sum-card-head');
            mHead.appendChild(makeEl('span', 'smp-sum-card-name', '🗺️ 巢区'));
            mHead.appendChild(makeEl('span', 'smp-sum-count', selectionS.size + ' 个站点'));
            mCard.appendChild(mHead);
            mCard.appendChild(makeChips(mNames, '未选择巢区'));
            summary.appendChild(mCard);

            const btns = makeEl('div', 'smp-sum-btns');
            const btnBack2 = makeEl('button', 'btn btn-outline btn-sm', '↩ 返回调整');
            const btnCancel = makeEl('button', 'btn btn-outline btn-sm', '✕ 取消');
            const btnOk = makeEl('button', 'btn btn-primary btn-sm', '✔ 确认');
            btnBack2.type = btnCancel.type = btnOk.type = 'button';
            btnBack2.addEventListener('click', function () { step = 2; canvas.style.display = ''; summary.style.display = 'none'; updateUI(); fit(); });
            btnCancel.addEventListener('click', function () { closePicker(null); });
            btnOk.addEventListener('click', function () {
                // 车多：只返回前 M 台（按选择顺序，与警告一致），并回传被移出数量
                const vehicles = Array.from(selectionV).slice(0, limit);
                closePicker(JSON.stringify({
                    Vehicles: vehicles,
                    Marks: Array.from(selectionS),
                    Dropped: excess ? selectionV.size - limit : 0
                }));
            });
            btns.appendChild(btnBack2);
            btns.appendChild(btnCancel);
            btns.appendChild(btnOk);
            summary.appendChild(btns);
        }

        // ── Tooltip ──
        function showTooltip(v, px, py) {
            tooltip.textContent = '';
            if (step === 1) {
                const st = vehicleStyle(v);
                tooltip.appendChild(makeEl('div', 'smp-tt-mark', '🚚 ' + v.Name + ' · ' + st.name));
                const detail = '位置 ' + (v.Location || '—') +
                    ' ｜ 电量 ' + Math.round((v.Power || 0) * 100) + '%' +
                    (v.IsReady ? '（可勾选）' : '（不可选：非就绪）');
                tooltip.appendChild(makeEl('div', 'smp-tt-type', detail));
            } else {
                if (isOccupied(v.Mark)) {
                    const vname = occupiedVehicleNames.get(String(v.Mark).trim().toLowerCase()) || '—';
                    tooltip.appendChild(makeEl('div', 'smp-tt-mark', '🚫 ' + v.Mark));
                    tooltip.appendChild(makeEl('div', 'smp-tt-type', '已被车辆 ' + vname + ' 占用（不可选）'));
                } else {
                    tooltip.appendChild(makeEl('div', 'smp-tt-mark', v.Mark));
                    tooltip.appendChild(makeEl('div', 'smp-tt-type',
                        decodeTypeNames(v.StationType).join(' + ') + (v.StaEnable ? '' : ' · 已禁用')));
                }
            }
            tooltip.style.display = 'block';
            const pad = 12;
            const tw = tooltip.offsetWidth, th = tooltip.offsetHeight;
            let tx = px + 12, ty = py - th - 10;
            if (tx + tw > wrap.clientWidth - pad) tx = px - tw - 12;
            if (ty < pad) ty = py + 14;
            tooltip.style.left = tx + 'px';
            tooltip.style.top = ty + 'px';
        }

        function hideTooltip() { tooltip.style.display = 'none'; }

        // ── 指针交互 ──
        function onDown(e) {
            if (e.button === 1 || e.button === 2) {
                const rect = canvas.getBoundingClientRect();
                drag = { mode: 'pan', x0: e.clientX - rect.left, y0: e.clientY - rect.top };
                canvas.classList.add('grabbing');
            } else {
                const rect = canvas.getBoundingClientRect();
                const px = e.clientX - rect.left, py = e.clientY - rect.top;
                const hit = hitTest(px, py);
                let clickKey = null;
                if (hit) {
                    if (step === 1) clickKey = hit.IsReady ? hit.Name : null;
                    else clickKey = (hit.StaEnable && !isOccupied(hit.Mark)) ? hit.Mark : null;
                }
                drag = {
                    mode: 'box', x0: px, y0: py, x1: px, y1: py, moved: false,
                    additive: !!(e.shiftKey || e.ctrlKey),
                    clickKey: clickKey
                };
            }
            try { canvas.setPointerCapture(e.pointerId); } catch (err) { }
        }

        function onMove(e) {
            const rect = canvas.getBoundingClientRect();
            const px = e.clientX - rect.left, py = e.clientY - rect.top;
            if (drag) {
                if (drag.mode === 'pan') {
                    view.ox += px - drag.x0;
                    view.oy += py - drag.y0;
                    drag.x0 = px; drag.y0 = py;
                    draw();
                } else {
                    drag.x1 = px; drag.y1 = py;
                    if (Math.hypot(px - drag.x0, py - drag.y0) > 4) drag.moved = true;
                    if (drag.moved) { hover = null; hideTooltip(); draw(); }
                }
                return;
            }
            const hit = hitTest(px, py);
            const prevKey = hover ? (step === 1 ? hover.Name : hover.Mark) : null;
            const curKey = hit ? (step === 1 ? hit.Name : hit.Mark) : null;
            if (curKey !== prevKey) { hover = hit; draw(); }
            if (hit) showTooltip(hit, px, py); else hideTooltip();
        }

        function onUp(e) {
            if (!drag) return;
            const rect = canvas.getBoundingClientRect();
            const px = e.clientX - rect.left, py = e.clientY - rect.top;
            const d = drag;
            drag = null;
            canvas.classList.remove('grabbing');
            if (d.mode === 'pan') { draw(); return; }
            if (!d.moved) {
                if (d.clickKey) {
                    if (step === 1) toggleIn(selectionV, d.clickKey);
                    else toggleIn(selectionS, d.clickKey);
                }
            } else {
                applyBox(d.x0, d.y0, px, py, d.additive);
            }
            draw();
            updateUI();
        }

        function onWheel(e) {
            e.preventDefault();
            const rect = canvas.getBoundingClientRect();
            const mx = e.clientX - rect.left, my = e.clientY - rect.top;
            const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
            const scale = view.scale;
            const ns = Math.min(Math.max(scale * factor, view.fitScale / 20), view.fitScale * 2000);
            const wx = (mx - view.ox) / scale, wy = (view.oy - my) / scale;
            view = { scale: ns, ox: mx - wx * ns, oy: my + wy * ns, fitScale: view.fitScale };
            draw();
        }

        // ── 按钮 / 关闭 ──
        function closePicker(result) {
            if (!S) return;
            window.removeEventListener('keydown', onKey);
            if (S.ro) S.ro.disconnect();
            const resolveFn = S.resolve;
            S = null;
            overlay.remove();
            resolveFn(result === null ? null : result);
        }

        function onKey(e) {
            if (e.key === 'Escape') { e.preventDefault(); closePicker(null); }
        }

        btnClear.addEventListener('click', function () {
            if (step === 1) { selectionV.clear(); }
            else { selectionS.clear(); preservedS.clear(); }
            updateUI();
            draw();
        });
        btnAll.addEventListener('click', function () {
            if (step === 1) { for (const v of vehicles) if (v.IsReady) selectionV.add(v.Name); }
            else { for (const p of currentPoints().enabled) selectionS.add(p.Mark); }
            updateUI();
            draw();
        });
        btnBack.addEventListener('click', function () { step = 1; updateUI(); fit(); });
        btnNext.addEventListener('click', function () { step = 2; updateUI(); fit(); });
        btnFinish.addEventListener('click', function () { showSummary(); });
        overlay.addEventListener('mousedown', function (e) { if (e.target === overlay) closePicker(null); });
        canvas.addEventListener('contextmenu', function (e) { e.preventDefault(); });
        canvas.addEventListener('pointerdown', onDown);
        canvas.addEventListener('pointermove', onMove);
        canvas.addEventListener('pointerup', onUp);
        canvas.addEventListener('pointercancel', onUp);
        canvas.addEventListener('pointerleave', function () {
            if (!drag) hideTooltip();
        });
        canvas.addEventListener('wheel', onWheel, { passive: false });
        window.addEventListener('keydown', onKey);

        const ro = new ResizeObserver(function () { requestAnimationFrame(fit); });
        ro.observe(wrap);

        S = { resolve: resolve, overlay: overlay, ro: ro };

        requestAnimationFrame(function () { updateUI(); fit(); });
    }
})();