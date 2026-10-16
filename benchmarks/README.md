# Terminal rendering benchmarks

These BenchmarkDotNet cases measure terminal image CPU time, managed allocations, and retained pixel storage. They use the headless Avalonia Skia renderer on CPU; they do not measure desktop GPU execution.

Run the suite in Release mode with:

```powershell
dotnet run -c Release --project benchmarks/SvcSystems.UI.Terminal.Benchmarks -- --filter "*Sixel*" "*Kitty*"
```

Run one protocol separately by replacing the filter with `*Sixel*` or `*Kitty*`.

The cases separate the work so a future change can be compared against the relevant cost:

| Benchmark | Work measured |
| --- | --- |
| `DecodeAndRefreshViewport` | Feed the Sixel payload into the terminal engine and rebuild visible viewport rows |
| `CopyDecodedStripPixels` | Copy an unchanged decoded strip into a caller-owned pixel buffer |
| `CachedBitmapHit` | Look up all visible strips in the bitmap cache without re-uploading them |
| `DecodeAndUploadChangedStrip` | Decode changed Sixel content and replace the changed visible bitmap cache entries |
| `RenderManyUniqueKittyImages` | Render 741 unique 10×20 Kitty images through the headless Skia CPU backend |
| `RenderManyPlacementsOfOneKittyImage` | Render 741 placements sharing one decoded Kitty image |

## Sixel baseline

Measured on Windows 11 with the .NET 10.0.13 x64 runtime, BenchmarkDotNet 0.15.2, and a ShortRun job (three measured iterations):

| Benchmark | Mean | Managed allocation per operation |
| --- | ---: | ---: |
| `DecodeAndRefreshViewport` | 485.8 µs | 19,281 B |
| `CopyDecodedStripPixels` | 2.40 µs | 0 B |
| `CachedBitmapHit` | 130.1 ns | 72 B |
| `DecodeAndUploadChangedStrip` | 513.2 µs | 20,705 B |

The ShortRun is an initial baseline, not a statistically strong release comparison. The complete BenchmarkDotNet output is in [`results/sixel-benchmark-net10-2026-10-14.md`](results/sixel-benchmark-net10-2026-10-14.md).

For this workload, decoded Sixel strips retain 768,000 pixel bytes and the visible bitmap cache retains another 768,000 pixel bytes: 1,536,000 bytes (about 1.46 MiB) total. That is the pixel storage tracked by the benchmark, not total process memory. `MemoryDiagnoser` reports managed allocations; it does not account for Avalonia native bitmap storage, GPU textures, driver memory, or allocator overhead. The headless benchmark also does not measure a desktop renderer's GPU execution time.

The current viewport is rebuilt on each feed. Each control's redraw interval limits model-driven invalidations, while unchanged bitmap cache hits avoid pixel copies and bitmap uploads; redraw pacing does not reduce terminal parsing or viewport refresh work per feed.

## Kitty baseline

`KittyGraphicsBenchmarks` compares rendering 741 distinct 10×20 RGBA pictures against rendering 741 placements of one shared picture. Both workloads fill the same 80×24-cell viewport. The shared-picture workload checks whether repeated placements reuse decoded pixels and the renderer bitmap cache.

Measured on Windows 11 with the .NET 10.0.13 x64 runtime, BenchmarkDotNet 0.15.2, and an eight-iteration job:

| Workload | Mean render time | Managed allocation per render | Decoded pixel bytes | Visible bitmap pixel bytes |
| --- | ---: | ---: | ---: | ---: |
| 741 unique pictures | 7.898 ms | 125.88 KB | 592,800 B | 592,800 B |
| 741 placements of one picture | 7.445 ms | 138.17 KB | 800 B | 800 B |

The shared workload retains 99.9% fewer decoded and cached image pixel bytes for this image set. The render-time confidence intervals overlap substantially, so this run does not establish a CPU-time difference. The shared workload allocated about 10% more managed memory per render; it trades that measured allocation for much lower retained image-pixel storage. Treat these figures as workload baselines, not evidence of a CPU improvement. The complete result is in [`results/kitty-benchmark-net10-2026-10-14.md`](results/kitty-benchmark-net10-2026-10-14.md).

Pixel-byte totals are calculated from image dimensions and the visible renderer cache. They are useful for comparing image retention but do not include bitmap metadata, allocator overhead, or any backend-specific texture storage. `MemoryDiagnoser` reports managed allocations only.
