//! Native starfield rasterizer for Emilio's Soundboard.
//!
//! The managed renderer draws every star with a separate GDI+ call. At a few hundred
//! stars (plus dust and halos) that becomes the most expensive part of the paint loop.
//! This module composites the whole layer into one premultiplied BGRA buffer, so the UI
//! only has to blit a single bitmap.
//!
//! The ABI is deliberately tiny and panic-safe. Any bad input returns a negative value
//! and the C# caller permanently falls back to the managed renderer.

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::slice;

/// Packed draw command for one star or dust mote.
/// 8 x f32 keeps the layout stable for P/Invoke (32 bytes).
#[repr(C)]
pub struct EsStar {
    pub x: f32,
    pub y: f32,
    pub radius: f32,
    pub r: f32,
    pub g: f32,
    pub b: f32,
    pub alpha: f32,
    pub halo: f32,
}

/// Returns the native ABI version. The C# loader uses this as a safe probe.
#[no_mangle]
pub extern "C" fn es_native_version() -> u32 {
    1
}

/// Renders `count` stars into a `width x height` 32-bit BGRA buffer.
///
/// `pixels` points at the first pixel of the top row, `stride_pixels` is the number of
/// pixels between rows (usually equal to width). The buffer is cleared first. Returns
/// 0 on success, -1 for invalid arguments, -2 if the renderer caught an internal panic.
#[no_mangle]
pub unsafe extern "C" fn es_render_starfield(
    pixels: *mut u32,
    width: i32,
    height: i32,
    stride_pixels: i32,
    stars: *const EsStar,
    count: i32,
) -> i32 {
    if pixels.is_null()
        || stars.is_null()
        || width <= 0
        || height <= 0
        || stride_pixels < width
        || count < 0
        || count > 100_000
    {
        return -1;
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        render(pixels, width, height, stride_pixels, stars, count);
    }));

    match result {
        Ok(()) => 0,
        Err(_) => -2,
    }
}

unsafe fn render(
    pixels: *mut u32,
    width: i32,
    height: i32,
    stride: i32,
    stars: *const EsStar,
    count: i32,
) {
    let pixel_count = stride as usize * height as usize;
    let buffer = slice::from_raw_parts_mut(pixels, pixel_count);
    buffer.fill(0);

    let commands = slice::from_raw_parts(stars, count as usize);
    for star in commands {
        if !star.alpha.is_finite()
            || !star.radius.is_finite()
            || star.alpha <= 0.002
            || star.radius <= 0.05
            || !star.x.is_finite()
            || !star.y.is_finite()
        {
            continue;
        }

        let radius = star.radius.clamp(0.05, 512.0);
        let alpha = star.alpha.clamp(0.0, 1.0);
        let halo = star.halo.clamp(0.0, 1.0);
        let r = star.r.clamp(0.0, 1.0);
        let g = star.g.clamp(0.0, 1.0);
        let b = star.b.clamp(0.0, 1.0);

        // A Gaussian core plus an optional wide, very faint halo. `support` is three
        // sigmas of the core or two sigma of the halo, whichever is larger.
        let core_sigma = (radius * 0.55).max(0.35);
        let halo_sigma = (radius * 2.15).max(1.0);
        let support = if halo > 0.0 {
            (halo_sigma * 2.2).max(core_sigma * 3.0) + 1.0
        } else {
            core_sigma * 3.0 + 1.0
        };

        let min_x = ((star.x - support).floor() as i32).max(0);
        let max_x = ((star.x + support).ceil() as i32).min(width - 1);
        let min_y = ((star.y - support).floor() as i32).max(0);
        let max_y = ((star.y + support).ceil() as i32).min(height - 1);
        if min_x > max_x || min_y > max_y {
            continue;
        }

        let core_inv = 1.0 / (2.0 * core_sigma * core_sigma);
        let halo_inv = 1.0 / (2.0 * halo_sigma * halo_sigma);
        let halo_strength = 0.20 * halo;

        for y in min_y..=max_y {
            let dy = y as f32 + 0.5 - star.y;
            let dy2 = dy * dy;
            let row = y as usize * stride as usize;
            for x in min_x..=max_x {
                let dx = x as f32 + 0.5 - star.x;
                let d2 = dx * dx + dy2;

                let core = (-d2 * core_inv).exp();
                let glow = if halo > 0.0 {
                    (-d2 * halo_inv).exp() * halo_strength
                } else {
                    0.0
                };
                let a = (core + glow) * alpha;
                if a <= 0.002 {
                    continue;
                }
                let a = a.min(1.0);

                // Normal premultiplied-alpha compositing. The target starts transparent,
                // but stars intentionally overlap into brighter clusters.
                let index = row + x as usize;
                let old = buffer[index];
                let old_a = ((old >> 24) & 0xFF) as f32 * (1.0 / 255.0);
                let old_r = ((old >> 16) & 0xFF) as f32 * (1.0 / 255.0);
                let old_g = ((old >> 8) & 0xFF) as f32 * (1.0 / 255.0);
                let old_b = (old & 0xFF) as f32 * (1.0 / 255.0);

                let inv = 1.0 - a;
                let out_a = a + old_a * inv;
                let out_r = r * a + old_r * inv;
                let out_g = g * a + old_g * inv;
                let out_b = b * a + old_b * inv;

                buffer[index] = (channel(out_a) << 24)
                    | (channel(out_r) << 16)
                    | (channel(out_g) << 8)
                    | channel(out_b);
            }
        }
    }
}

#[inline]
fn channel(value: f32) -> u32 {
    (value.clamp(0.0, 1.0) * 255.0 + 0.5) as u32
}
