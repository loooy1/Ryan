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