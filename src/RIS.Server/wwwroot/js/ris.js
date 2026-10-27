// RIS 5 UI helpers: preferences cookie, file download, export of SVG charts to PNG, printing.
window.ris = {
    setCookie: (name, value, days) => {
        const expires = new Date(Date.now() + days * 864e5).toUTCString();
        document.cookie = `${name}=${encodeURIComponent(value)}; expires=${expires}; path=/; SameSite=Lax`;
    },

    downloadFile: async (fileName, contentType, streamReference) => {
        const buffer = await streamReference.arrayBuffer();
        const url = URL.createObjectURL(new Blob([buffer], { type: contentType }));
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName;
        a.click();
        URL.revokeObjectURL(url);
    },

    // Copies computed styles into a clone of the SVG (theme colours come from CSS variables), renders it on a canvas, downloads PNG.
    exportSvgToPng: (elementId, fileName) => {
        const svg = document.getElementById(elementId);
        if (!svg) return;
        const clone = svg.cloneNode(true);
        const source = svg.querySelectorAll("*");
        const target = clone.querySelectorAll("*");
        const properties = ["fill", "fill-opacity", "stroke", "stroke-width", "stroke-dasharray", "opacity",
            "font-size", "font-family", "font-weight", "text-anchor", "dominant-baseline", "display"];
        source.forEach((node, i) => {
            const style = getComputedStyle(node);
            target[i].setAttribute("style", properties.map(p => `${p}:${style.getPropertyValue(p)}`).join(";"));
        });
        const box = svg.getBoundingClientRect();
        const width = Math.ceil(box.width);
        const height = Math.ceil(box.height);
        clone.setAttribute("width", width);
        clone.setAttribute("height", height);
        const background = getComputedStyle(document.querySelector(".mud-main-content") || document.body).backgroundColor;
        const image = new Image();
        image.onload = () => {
            const scale = 2;
            const canvas = document.createElement("canvas");
            canvas.width = width * scale;
            canvas.height = height * scale;
            const ctx = canvas.getContext("2d");
            ctx.fillStyle = background;
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            ctx.scale(scale, scale);
            ctx.drawImage(image, 0, 0);
            canvas.toBlob(blob => {
                const a = document.createElement("a");
                a.href = URL.createObjectURL(blob);
                a.download = fileName;
                a.click();
                setTimeout(() => URL.revokeObjectURL(a.href), 1000);
            }, "image/png");
        };
        image.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(new XMLSerializer().serializeToString(clone));
    },

    print: () => window.print()
};
