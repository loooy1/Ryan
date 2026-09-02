// ════════════════════════════════════════════════════════════════════════
// 站点地图框选器（StationMapPicker）
// 用途：自动化任务「选点范围」→「手动指定站点」的地图框选入口。
// 数据流：C# 组装候选站点（启用 + 类型位过滤 + 楼层）与既有白名单 → 传 JSON 给
// grcsStationMapPickerOpen → JS 侧全量渲染 Canvas 散点图，交互（框选/平移/缩放/
// 单击/tooltip）全部在本文件内完成，避免 WASM 频繁重渲染 → 确定后 resolve
// 选中的 Mark 数组（JSON 字符串，取消为 null）→ C# 写回 _rangeMarksText 并持久化。
// 交互约定：左键拖拽=框选（替换）；Shift/Ctrl 拖拽=增选/减选；单击站点=切换；
// 右键/中键拖拽=平移；滚轮=缩放；Esc/遮罩点击=取消。
// 数据口径：候选集不含 Marks 白名单自身（否则越选越窄）；禁用站点置灰不可选；
// 候选集外的既有手输 Mark 在替换框选时保留（增量编辑不丢数据）。
// ════════════════════════════════════════════════════════════════════════
(function () {
    'use strict';

    // 类型位 → 主色/图例名（位值与 C# MapStationTypeBits 一致；优先顺序决定主色）
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

    function makeEl(tag, cls, text) {
        const el = document.createElement(tag);
        if (cls) el.className = cls;
        if (text !== undefined && text !== null) el.textContent = text;
        return el;
    }

    // 当前实例（同一时刻只允许一个框选器）
    let S = null;

    /**
     * 打开站点地图框选器。
     * @param {string} configJson StationMapPickerConfig 的 JSON：
     *   { Floors:number[], InitialFloor:number, Stations:[{Mark,StationType,X,Y,Floor,StaEnable}], Preselected:string[] }
     * @returns {Promise<string|null>} 确定→选中 Mark 数组的 JSON 字符串；取消→null。
     */
    window.grcsStationMapPickerOpen = function (configJson) {
        return new Promise(function (resolve) {
            let config;
            try { config = JSON.parse(configJson); } catch (e) { config = null; }
            if (!config || !Array.isArray(config.Stations) || !Array.isArray(config.Floors) || config.Floors.length === 0) {
                resolve(null);
                return;
            }
            if (S) { // 防御：上一次未关闭（理论上不会发生）
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
        toolbar.appendChild(makeEl('div', 'smp-title', '🗺️ 地图框选 · 手动指定站点'));
        const floorsWrap = makeEl('div', 'smp-floors');
        const chips = config.Floors.map(function (f) {
            const b = makeEl('button', 'chip-btn', f + ' 层');
            b.type = 'button';
            b.addEventListener('click', function () { setCurrentFloor(f); });
            floorsWrap.appendChild(b);
            return { floor: f, el: b };
        });
        toolbar.appendChild(floorsWrap);
        const countEl = makeEl('span', 'smp-count', '已选 0');
        toolbar.appendChild(countEl);
        const btnClear = makeEl('button', 'btn btn-outline btn-sm', '🗑 清空');
        const btnAll = makeEl('button', 'btn btn-outline btn-sm', '☑ 全选本层');
        const btnCancel = makeEl('button', 'btn btn-outline btn-sm', '✕ 取消');
        const btnOk = makeEl('button', 'btn btn-primary btn-sm', '✔ 确定');
        btnClear.type = btnAll.type = btnCancel.type = btnOk.type = 'button';
        toolbar.appendChild(btnClear);
        toolbar.appendChild(btnAll);
        toolbar.appendChild(btnCancel);
        toolbar.appendChild(btnOk);
        modal.appendChild(toolbar);

        const wrap = makeEl('div', 'smp-canvas-wrap');
        const canvas = makeEl('canvas', 'smp-canvas');
        wrap.appendChild(canvas);
        const tooltip = makeEl('div', 'smp-tooltip');
        tooltip.style.display = 'none';
        wrap.appendChild(tooltip);
        modal.appendChild(wrap);

        const legend = makeEl('div', 'smp-legend');
        modal.appendChild(legend);
        modal.appendChild(makeEl('div', 'smp-hint',
            '左键拖拽=框选(替换) · Shift/Ctrl 拖拽=增选/减选 · 单击站点=切换 · 右键/中键拖拽=平移 · 滚轮=缩放 · Esc/遮罩=取消'));

        overlay.appendChild(modal);
        document.body.appendChild(overlay);

        const ctx = canvas.getContext('2d');

        // ── 状态 ──
        const byFloor = new Map();
        for (const st of config.Stations) {
            if (!byFloor.has(st.Floor)) byFloor.set(st.Floor, { enabled: [], disabled: [] });
            const bucket = byFloor.get(st.Floor);
            (st.StaEnable ? bucket.enabled : bucket.disabled).push(st);
        }
        const candidateMarks = new Set(config.Stations.map(function (s) { return s.Mark; }));
        const selection = new Set((config.Preselected || []).map(function (m) { return String(m).trim(); }).filter(Boolean));
        // 候选集外的既有手输 Mark（如已禁用/类型过滤外的站点）：替换框选时保留，避免增量编辑丢数据
        const preserved = new Set(Array.from(selection).filter(function (m) { return !candidateMarks.has(m); }));
        let currentFloor = config.InitialFloor !== undefined && config.Floors.indexOf(config.InitialFloor) >= 0
            ? config.InitialFloor : config.Floors[0];
        let view = { scale: 1, ox: 0, oy: 0, fitScale: 1 };
        let drag = null;
        let hover = null;

        function setCurrentFloor(f) {
            currentFloor = f;
            chips.forEach(function (c) { c.el.classList.toggle('on', c.floor === f); });
            fit();
            buildLegend();
            updateCount();
        }

        function fit() {
            const w = canvas.clientWidth, h = canvas.clientHeight;
            const pts = byFloor.get(currentFloor);
            if (!pts || pts.enabled.length + pts.disabled.length === 0) {
                view = { scale: 1, ox: 0, oy: 0, fitScale: 1 };
                draw();
                return;
            }
            if (w === 0 || h === 0) { requestAnimationFrame(fit); return; }
            const all = pts.enabled.concat(pts.disabled);
            let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
            for (const p of all) {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            }
            const spanX = maxX - minX, spanY = maxY - minY;
            const pad = 56;
            let scale = Math.min((w - pad * 2) / (spanX || 1), (h - pad * 2) / (spanY || 1));
            if (!isFinite(scale) || scale <= 0) scale = 1;
            if (spanX === 0 && spanY === 0) scale = Math.max(w, h) / 16; // 单点
            const cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            // 地图为左下角原点、Y 向上的笛卡尔系：屏幕 Y 需翻转（sy = oy - Y*scale）
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

            const pts = byFloor.get(currentFloor);
            if (!pts || pts.enabled.length + pts.disabled.length === 0) {
                ctx.fillStyle = '#64748b';
                ctx.font = '13px sans-serif';
                ctx.textAlign = 'center';
                ctx.fillText('本层无站点', w / 2, h / 2);
                return;
            }

            const { scale, ox, oy } = view;
            drawGrid(w, h);

            const visible = [];
            for (const p of pts.enabled.concat(pts.disabled)) {
                // 屏幕 Y = oy - Y*scale（笛卡尔 Y 向上 → 屏幕 Y 向下）
                const sx = p.X * scale + ox, sy = oy - p.Y * scale;
                if (sx < -30 || sx > w + 30 || sy < -30 || sy > h + 30) continue;
                visible.push({ p: p, sx: sx, sy: sy });
            }

            const inBox = drag && drag.mode === 'box' && drag.moved;

            // 禁用站点：置灰、不可选（先画垫底）
            for (const v of visible) {
                if (v.p.StaEnable) continue;
                drawPoint(v.p, v.sx, v.sy, false, '#475569', 3.5, 0, null, false);
            }
            // 启用站点：按类型着色，选中/框选命中描边高亮
            for (const v of visible) {
                if (!v.p.StaEnable) continue;
                const isSel = selection.has(v.p.Mark);
                const isHit = inBox ? rectContains(drag, v.sx, v.sy) : false;
                const isHover = hover !== null && hover.Mark === v.p.Mark;
                const col = primaryType(v.p.StationType).color;
                if (isSel)
                    drawPoint(v.p, v.sx, v.sy, true, col, 8.5, 3, '#ffffff', true);
                else if (isHit)
                    drawPoint(v.p, v.sx, v.sy, true, col, 7, 2, 'rgba(255,255,255,0.85)', true);
                else if (isHover)
                    drawPoint(v.p, v.sx, v.sy, false, col, 5.5, 2, '#ffffff', false);
                else
                    drawPoint(v.p, v.sx, v.sy, false, col, 4.5, 1.6, 'rgba(255,255,255,0.7)', false);
            }

            // 橡皮筋矩形
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

        function drawPoint(p, sx, sy, highlight, color, radius, ringWidth, ringColor, withLabel) {
            if (highlight) {
                // 选中/命中：先画外光晕（半透明青环），远看即与未选点区分
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
            if (withLabel) {
                ctx.fillStyle = 'rgba(226,232,240,0.95)';
                ctx.font = '10px Consolas, monospace';
                ctx.textAlign = 'left';
                ctx.fillText(p.Mark, sx + radius + 5, sy + 3);
            }
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
            // 水平网格线：屏幕 sy = oy - j*step*scale（Y 翻转）
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
            const pts = byFloor.get(currentFloor);
            if (!pts) return null;
            const { scale, ox, oy } = view;
            let best = null, bestD = Infinity;
            const all = pts.enabled.concat(pts.disabled);
            for (const p of all) {
                const sx = p.X * scale + ox, sy = oy - p.Y * scale;
                const d = Math.hypot(sx - px, sy - py);
                if (d < bestD) { bestD = d; best = p; }
            }
            return bestD <= 10 ? best : null;
        }

        // ── 选中操作 ──
        function toggleMark(m) {
            if (selection.has(m)) selection.delete(m); else selection.add(m);
        }

        function applyBox(x0, y0, x1, y1, additive) {
            const pts = byFloor.get(currentFloor);
            if (!pts) return;
            const ax = Math.min(x0, x1), bx = Math.max(x0, x1);
            const ay = Math.min(y0, y1), by = Math.max(y0, y1);
            const { scale, ox, oy } = view;
            const hits = pts.enabled.filter(function (p) {
                const sx = p.X * scale + ox, sy = oy - p.Y * scale;
                return sx >= ax && sx <= bx && sy >= ay && sy <= by;
            }).map(function (p) { return p.Mark; });
            if (additive) {
                for (const m of hits) toggleMark(m);
            } else {
                selection.clear();
                for (const m of hits) selection.add(m);
                // 保留候选集外的既有手输 Mark（增量编辑不丢数据）
                for (const m of preserved) selection.add(m);
            }
        }

        function updateCount() {
            countEl.textContent = '已选 ' + selection.size;
        }

        function buildLegend() {
            legend.textContent = '';
            const pts = byFloor.get(currentFloor);
            if (!pts) return;
            const types = new Map();
            for (const p of pts.enabled.concat(pts.disabled)) {
                const t = primaryType(p.StationType);
                if (!types.has(t.name)) types.set(t.name, t.color);
            }
            for (const entry of types) {
                const item = makeEl('div', 'smp-legend-item');
                const dot = makeEl('span', 'smp-legend-dot');
                dot.style.background = entry[1];
                item.appendChild(dot);
                item.appendChild(document.createTextNode(entry[0]));
                legend.appendChild(item);
            }
        }

        // ── Tooltip ──
        function showTooltip(p, px, py) {
            tooltip.textContent = '';
            tooltip.appendChild(makeEl('div', 'smp-tt-mark', p.Mark));
            tooltip.appendChild(makeEl('div', 'smp-tt-type',
                decodeTypeNames(p.StationType).join(' + ') + (p.StaEnable ? '' : ' · 已禁用')));
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
                drag = {
                    mode: 'box', x0: px, y0: py, x1: px, y1: py, moved: false,
                    additive: !!(e.shiftKey || e.ctrlKey),
                    clickMark: hit && hit.StaEnable ? hit.Mark : null
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
            const prevMark = hover ? hover.Mark : null;
            const curMark = hit ? hit.Mark : null;
            if (curMark !== prevMark) { hover = hit; draw(); }
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
                if (d.clickMark) toggleMark(d.clickMark); // 单击站点 = 切换选中
            } else {
                applyBox(d.x0, d.y0, px, py, d.additive);
            }
            draw();
            updateCount();
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
            resolveFn(result === null ? null : JSON.stringify(result));
        }

        function onKey(e) {
            if (e.key === 'Escape') { e.preventDefault(); closePicker(null); }
        }

        btnClear.addEventListener('click', function () {
            selection.clear();
            preserved.clear(); // 清空 = 全部清掉（含候选集外的手输 Mark）
            updateCount();
            draw();
        });
        btnAll.addEventListener('click', function () {
            const pts = byFloor.get(currentFloor);
            if (pts) for (const p of pts.enabled) selection.add(p.Mark);
            updateCount();
            draw();
        });
        btnCancel.addEventListener('click', function () { closePicker(null); });
        btnOk.addEventListener('click', function () { closePicker(Array.from(selection)); });
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

        requestAnimationFrame(function () { setCurrentFloor(currentFloor); });
    }
})();