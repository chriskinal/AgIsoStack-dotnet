// Reference painter for the terminal's draw list (docs/API.md), Canvas 2D. MIT.
// No ISOBUS knowledge: it paints the items in order. AgOpenWeb's page starts from this.
//
//   const images = new VtImages();             // keep one per terminal
//   images.add(id, width, height, rgbaBytes);  // from the image callback, before the screen
//   vtPaintMask(ctx, screen, images);          // the data or alarm mask, at 0,0 in mask pixels
//   vtPaintKey(ctx, screen.keys, i, images);   // one soft key cell, at 0,0 in key pixels
//   vtHit(screen.items, x, y)                  // the touchable item under a point, or null
"use strict";

class VtImages {
  constructor() { this.byId = new Map(); }
  add(id, w, h, rgba) {
    const c = document.createElement("canvas");
    c.width = w;
    c.height = h;
    c.getContext("2d").putImageData(new ImageData(new Uint8ClampedArray(rgba), w, h), 0, 0);
    this.byId.set(id, c);
  }
  get(id) { return this.byId.get(id); }
}

// The 16 pixel line art pattern as dash lengths, most significant bit first.
function vtDash(pattern) {
  const runs = [];
  let bit = (pattern >> 15) & 1, run = 0;
  if (!bit) runs.push(0); // a dash list starts with a drawn run
  for (let i = 15; i >= 0; i--) {
    const b = (pattern >> i) & 1;
    if (b === bit) run++;
    else { runs.push(run); bit = b; run = 1; }
  }
  runs.push(run);
  return runs;
}

function vtStroke(ctx, it) {
  if (!it.line || !it.lw) return false;
  ctx.strokeStyle = it.line;
  ctx.lineWidth = it.lw;
  ctx.setLineDash(it.dash !== undefined ? vtDash(it.dash) : []);
  return true;
}

function vtFill(ctx, it, images) {
  if (it.pat) {
    const img = images.get(it.pat);
    if (!img) return false;
    ctx.fillStyle = ctx.createPattern(img, "repeat");
    return true;
  }
  if (!it.fill) return false;
  ctx.fillStyle = it.fill;
  return true;
}

// Text in a box of fixed font cells: a monospace face stretched to the cell.
function vtText(ctx, it) {
  const style = it.style || 0;
  let c = it.c || "#000000", bg = it.bg;
  if (style & 16 && bg) [c, bg] = [bg, c]; // inverted
  if (bg) { ctx.fillStyle = bg; ctx.fillRect(it.x, it.y, it.w, it.h); }
  const fw = it.fw || 8, fh = it.fh || 8;
  const perLine = Math.max(1, Math.floor(it.w / fw));
  let lines = [];
  for (const para of it.s.split(/\r\n|\n|\r/)) {
    if (!it.wrap || para.length <= perLine) { lines.push(para); continue; }
    let rest = para;
    while (rest.length > perLine) {
      let cut = rest.lastIndexOf(" ", perLine);
      if (cut <= 0) cut = perLine;
      lines.push(rest.slice(0, cut));
      rest = rest.slice(cut).replace(/^ /, "");
    }
    lines.push(rest);
  }
  lines = lines.slice(0, Math.max(1, Math.floor(it.h / fh)));
  const total = lines.length * fh;
  let y = it.vj === 1 ? it.y + (it.h - total) / 2 : it.vj === 2 ? it.y + it.h - total : it.y;
  ctx.save();
  ctx.beginPath();
  ctx.rect(it.x, it.y, it.w, it.h);
  ctx.clip();
  ctx.font = `${style & 8 ? "italic " : ""}${style & 1 ? "bold " : ""}${fh}px ui-monospace, Menlo, Consolas, monospace`;
  ctx.textBaseline = "alphabetic";
  const advance = ctx.measureText("M").width || fh / 2;
  const sx = fw / advance;
  ctx.fillStyle = c;
  ctx.strokeStyle = c;
  ctx.lineWidth = Math.max(1, fh / 12);
  for (const line of lines) {
    const width = line.length * fw;
    const x = it.hj === 1 ? it.x + (it.w - width) / 2 : it.hj === 2 ? it.x + it.w - width : it.x;
    ctx.save();
    ctx.translate(x, y + fh * 0.8);
    ctx.scale(sx, 1);
    ctx.fillText(line, 0, 0);
    ctx.restore();
    if (style & 4) { ctx.beginPath(); ctx.moveTo(x, y + fh - 1); ctx.lineTo(x + width, y + fh - 1); ctx.stroke(); }
    if (style & 2) { ctx.beginPath(); ctx.moveTo(x, y + fh / 2); ctx.lineTo(x + width, y + fh / 2); ctx.stroke(); }
    y += fh;
  }
  ctx.restore();
}

function vtItem(ctx, it, images) {
  switch (it.t) {
    case "clip":
      ctx.save();
      ctx.beginPath();
      ctx.rect(it.x, it.y, it.w, it.h);
      ctx.clip();
      break;
    case "unclip":
      ctx.restore();
      break;
    case "rect": {
      if (vtFill(ctx, it, images)) ctx.fillRect(it.x, it.y, it.w, it.h);
      if (!vtStroke(ctx, it)) break;
      // The line lies inside the rectangle.
      const h = it.lw / 2, x0 = it.x + h, y0 = it.y + h, x1 = it.x + it.w - h, y1 = it.y + it.h - h;
      const sides = it.sides === undefined ? 15 : it.sides;
      if (sides === 15) { ctx.strokeRect(x0, y0, x1 - x0, y1 - y0); break; }
      ctx.beginPath();
      if (sides & 1) { ctx.moveTo(x0, y0); ctx.lineTo(x1, y0); }
      if (sides & 2) { ctx.moveTo(x1, y0); ctx.lineTo(x1, y1); }
      if (sides & 4) { ctx.moveTo(x0, y1); ctx.lineTo(x1, y1); }
      if (sides & 8) { ctx.moveTo(x0, y0); ctx.lineTo(x0, y1); }
      ctx.stroke();
      break;
    }
    case "line":
      if (!vtStroke(ctx, it)) break;
      ctx.beginPath();
      ctx.moveTo(it.x1, it.y1);
      ctx.lineTo(it.x2, it.y2);
      ctx.stroke();
      break;
    case "ellipse": {
      // Angles count anticlockwise from 3 o'clock; the canvas's y axis points down.
      const rx = it.w / 2, ry = it.h / 2, cx = it.x + rx, cy = it.y + ry;
      const kind = it.kind || 0;
      const full = !kind || it.a0 === it.a1;
      const a0 = full ? 0 : (-it.a0 * Math.PI) / 180, a1 = full ? 2 * Math.PI : (-it.a1 * Math.PI) / 180;
      ctx.beginPath();
      if (kind === 3 && !full) ctx.moveTo(cx, cy);
      ctx.ellipse(cx, cy, Math.max(0, rx - (it.lw || 0) / 2), Math.max(0, ry - (it.lw || 0) / 2), 0, a0, a1, !full);
      if (kind !== 1) ctx.closePath();
      if (kind !== 1 && vtFill(ctx, it, images)) ctx.fill();
      if (vtStroke(ctx, it)) ctx.stroke();
      break;
    }
    case "poly": {
      const p = it.pts;
      if (p.length < 4) break;
      ctx.beginPath();
      ctx.moveTo(p[0], p[1]);
      for (let i = 2; i < p.length; i += 2) ctx.lineTo(p[i], p[i + 1]);
      if (it.closed) ctx.closePath();
      if (it.closed && vtFill(ctx, it, images)) ctx.fill("evenodd");
      if (vtStroke(ctx, it)) ctx.stroke();
      break;
    }
    case "text":
      vtText(ctx, it);
      break;
    case "image": {
      const img = images.get(it.img);
      if (img) { ctx.imageSmoothingEnabled = false; ctx.drawImage(img, it.x, it.y, it.w, it.h); }
      break;
    }
    case "hit":
      if (!it.on) { ctx.fillStyle = "rgba(128,128,128,0.5)"; ctx.fillRect(it.x, it.y, it.w, it.h); }
      break;
    case "box":
      ctx.setLineDash([4, 3]);
      ctx.strokeStyle = "#ff00ff";
      ctx.lineWidth = 1;
      ctx.strokeRect(it.x + 0.5, it.y + 0.5, it.w - 1, it.h - 1);
      ctx.setLineDash([]);
      ctx.fillStyle = "#ff00ff";
      ctx.font = "10px sans-serif";
      ctx.fillText(it.label, it.x + 3, it.y + 12);
      break;
  }
}

function vtPaintItems(ctx, items, images) {
  ctx.save();
  for (const it of items) vtItem(ctx, it, images);
  ctx.restore();
}

function vtPaintMask(ctx, screen, images) {
  ctx.fillStyle = screen.bg || "#000000";
  ctx.fillRect(0, 0, screen.w, screen.h);
  ctx.save();
  ctx.beginPath();
  ctx.rect(0, 0, screen.w, screen.h);
  ctx.clip();
  vtPaintItems(ctx, screen.items, images);
  ctx.restore();
}

// Soft key i of the screen's key mask; an empty place (null) shows the mask's background.
function vtPaintKey(ctx, keys, i, images) {
  const key = keys.keys[i];
  ctx.fillStyle = key ? key.bg : keys.bg;
  ctx.fillRect(0, 0, keys.w, keys.h);
  if (!key) return;
  ctx.save();
  ctx.beginPath();
  ctx.rect(0, 0, keys.w, keys.h);
  ctx.clip();
  vtPaintItems(ctx, key.items, images);
  ctx.restore();
}

// The topmost touchable item at x, y (mask pixels), or null.
function vtHit(items, x, y) {
  for (let i = items.length - 1; i >= 0; i--) {
    const it = items[i];
    if (it.t === "hit" && x >= it.x && x < it.x + it.w && y >= it.y && y < it.y + it.h) return it;
  }
  return null;
}
