// 通用文本文件下载（CSV 等）：UTF-8 编码，BOM 由调用方内容自带（首个字符 \uFEFF），避免 Excel 中文乱码
window.downloadTextFile = function (filename, content, mime) {
    mime = mime || "text/plain";
    const blob = new Blob([content], { type: mime });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
};

// 通用二进制下载（xlsx 等）：传入 base64 字符串
window.downloadBase64 = function (filename, base64, mime) {
    mime = mime || "application/octet-stream";
    const bin = atob(base64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    const blob = new Blob([bytes], { type: mime });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
};

// Render project records to paginated canvas pages and download a real PDF directly.
// Raster pages keep Chinese text readable without adding a server-side PDF/font dependency.
window.downloadProjectPdf = function (filename, documentData) {
    const pageWidth = 1240;
    const pageHeight = 1754;
    const margin = 92;
    const bottom = pageHeight - 105;
    const contentWidth = pageWidth - margin * 2;
    const pages = [];
    let canvas;
    let context;
    let y;

    const startPage = () => {
        canvas = document.createElement("canvas");
        canvas.width = pageWidth;
        canvas.height = pageHeight;
        context = canvas.getContext("2d", { alpha: false });
        context.fillStyle = "#ffffff";
        context.fillRect(0, 0, pageWidth, pageHeight);
        pages.push(canvas);
        y = margin;
        return context;
    };

    const ensureRoom = (height) => {
        if (y + height > bottom) {
            startPage();
            y = margin;
        }
    };

    const wrappedLines = (text, font) => {
        context.font = font;
        const result = [];
        let line = "";
        for (const char of Array.from(text)) {
            const next = line + char;
            if (line && context.measureText(next).width > contentWidth) {
                result.push(line);
                line = char;
            } else {
                line = next;
            }
        }
        if (line || result.length === 0) result.push(line);
        return result;
    };

    const drawText = (text, { font = "22px Arial, 'Microsoft YaHei', sans-serif", color = "#26364b", lineHeight = 36, indent = 0, background = null } = {}) => {
        const lines = wrappedLines(text, font);
        for (const line of lines) {
            ensureRoom(lineHeight);
            if (background) {
                context.fillStyle = background;
                context.fillRect(margin - 10, y - 2, pageWidth - margin * 2 + 20, lineHeight + 4);
            }
            context.font = font;
            context.fillStyle = color;
            context.textBaseline = "top";
            context.fillText(line, margin + indent, y, contentWidth - indent);
            y += lineHeight;
        }
    };

    startPage();
    context.fillStyle = "#087eaa";
    context.font = "600 18px 'Segoe UI', sans-serif";
    context.textBaseline = "top";
    context.fillText("PROJECT WORKSPACE", margin, y);
    y += 38;
    drawText(documentData.title || "项目记录", { font: "700 42px Arial, 'Microsoft YaHei', sans-serif", color: "#17243b", lineHeight: 58 });
    drawText(`项目资料汇总 · 导出时间：${documentData.exportTime || ""}`, { font: "20px Arial, 'Microsoft YaHei', sans-serif", color: "#61718a", lineHeight: 34 });
    y += 28;
    context.strokeStyle = "#aebdcd";
    context.lineWidth = 2;
    context.beginPath();
    context.moveTo(margin, y);
    context.lineTo(pageWidth - margin, y);
    context.stroke();
    y += 38;

    const sections = documentData.sections || [];
    sections.forEach((section, sectionIndex) => {
        ensureRoom(90);
        if (sectionIndex > 0) {
            y += 16;
            context.strokeStyle = "#cbd8e6";
            context.lineWidth = 2;
            context.beginPath();
            context.moveTo(margin, y);
            context.lineTo(pageWidth - margin, y);
            context.stroke();
            y += 8;
            context.beginPath();
            context.moveTo(margin, y);
            context.lineTo(pageWidth - margin, y);
            context.stroke();
            y += 26;
        }
        drawText(`${section.icon || ""} ${section.title || "资料分区"}`.trim(), {
            font: "700 28px Arial, 'Microsoft YaHei', sans-serif",
            color: "#123d5a",
            lineHeight: 44
        });
        y += 6;

        const lines = String(section.content || "暂无内容").replace(/\r\n?/g, "\n").split("\n");
        const hasFence = lines.some(line => /^\s*(```|~~~)/.test(line));
        let inCode = false;
        for (const raw of lines) {
            const line = raw.trim();
            if (/^(```|~~~)/.test(line)) {
                if (hasFence) inCode = !inCode;
                y += 4;
                continue;
            }
            if (!line) {
                y += 12;
                continue;
            }
            if (inCode) {
                drawText(line, { lineHeight: 34, indent: 16, background: "#eef3f8" });
            } else if (/^#{1,3}\s/.test(line)) {
                drawText(line.replace(/^#{1,3}\s+/, ""), { font: "700 26px Arial, 'Microsoft YaHei', sans-serif", color: "#243e5a", lineHeight: 40 });
                y += 3;
            } else if (/^[-*]\s/.test(line)) {
                drawText(`•  ${line.slice(2)}`, { lineHeight: 34, indent: 12 });
            } else if (/^>\s?/.test(line)) {
                drawText(`— ${line.replace(/^>\s?/, "")}`, { color: "#52657c", lineHeight: 34, indent: 12 });
            } else {
                drawText(line, { lineHeight: 34 });
            }
        }
        y += 14;
    });

    pages.forEach((page, index) => {
        const ctx = page.getContext("2d");
        ctx.fillStyle = "#718096";
        ctx.font = "16px 'Segoe UI', sans-serif";
        ctx.textBaseline = "alphabetic";
        ctx.fillText(`${index + 1} / ${pages.length}`, pageWidth - margin - 70, pageHeight - 42);
    });

    const encoder = new TextEncoder();
    const ascii = (value) => encoder.encode(value);
    const objects = [];
    const pageIds = pages.map((_, index) => 3 + index * 3);
    objects[1] = ascii("<< /Type /Catalog /Pages 2 0 R >>");
    objects[2] = ascii(`<< /Type /Pages /Kids [${pageIds.map(id => `${id} 0 R`).join(" ")}] /Count ${pages.length} >>`);

    pages.forEach((page, index) => {
        const pageId = pageIds[index];
        const imageId = pageId + 1;
        const contentId = pageId + 2;
        const jpegBase64 = page.toDataURL("image/jpeg", 0.9).split(",")[1];
        const jpegBinary = atob(jpegBase64);
        const jpegBytes = new Uint8Array(jpegBinary.length);
        for (let i = 0; i < jpegBinary.length; i++) jpegBytes[i] = jpegBinary.charCodeAt(i);

        objects[pageId] = ascii(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Resources << /XObject << /Im0 ${imageId} 0 R >> >> /Contents ${contentId} 0 R >>`);
        objects[imageId] = { stream: jpegBytes, header: ascii(`<< /Type /XObject /Subtype /Image /Width ${pageWidth} /Height ${pageHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ${jpegBytes.length} >>\nstream\n`), footer: ascii("\nendstream") };
        const drawing = ascii(`q\n595.28 0 0 841.89 0 0 cm\n/Im0 Do\nQ\n`);
        objects[contentId] = { stream: drawing, header: ascii(`<< /Length ${drawing.length} >>\nstream\n`), footer: ascii("endstream") };
    });

    const chunks = [ascii("%PDF-1.4\n")];
    const offsets = new Array(objects.length).fill(0);
    let byteOffset = chunks[0].length;
    for (let id = 1; id < objects.length; id++) {
        const object = objects[id];
        offsets[id] = byteOffset;
        const prefix = ascii(`${id} 0 obj\n`);
        const suffix = ascii("\nendobj\n");
        chunks.push(prefix);
        byteOffset += prefix.length;
        if (object.stream) {
            chunks.push(object.header, object.stream, object.footer);
            byteOffset += object.header.length + object.stream.length + object.footer.length;
        } else {
            chunks.push(object);
            byteOffset += object.length;
        }
        chunks.push(suffix);
        byteOffset += suffix.length;
    }
    const xrefOffset = byteOffset;
    let xref = `xref\n0 ${objects.length}\n0000000000 65535 f \n`;
    for (let id = 1; id < objects.length; id++) xref += `${String(offsets[id]).padStart(10, "0")} 00000 n \n`;
    xref += `trailer\n<< /Size ${objects.length} /Root 1 0 R >>\nstartxref\n${xrefOffset}\n%%EOF`;
    chunks.push(ascii(xref));

    const pdf = new Uint8Array(chunks.reduce((sum, chunk) => sum + chunk.length, 0));
    let offset = 0;
    for (const chunk of chunks) {
        pdf.set(chunk, offset);
        offset += chunk.length;
    }
    const url = URL.createObjectURL(new Blob([pdf], { type: "application/pdf" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 30000);
};
