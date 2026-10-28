# emilios_native

Optional Rust `cdylib` that rasterizes the animated starfield into one premultiplied
BGRA buffer. The C# app probes it once and keeps using its managed GDI+ renderer if the
DLL is missing or returns an error.

## Build

From the solution root:

```bat
BUILD-NATIVE.bat
```

or directly:

```bat
cargo build --release --manifest-path native\emilios_native\Cargo.toml
copy native\emilios_native\target\release\emilios_native.dll EmiliosSoundboard\runtimes\win-x64\native\
```

The checked-in DLL is copied to the app output by `EmiliosSoundboard.csproj`. It has no
third-party Rust dependencies; only the Windows UCRT/Kernel32 are imported.

## ABI

- `es_native_version() -> u32`
- `es_render_starfield(pixels, width, height, stride_pixels, stars, count) -> i32`

`stars` is an array of eight `f32` values per star: `x`, `y`, `radius`, `r`, `g`, `b`,
`alpha`, `halo`. The pixel buffer is 32-bit BGRA (`Format32bppPArgb` in .NET).
