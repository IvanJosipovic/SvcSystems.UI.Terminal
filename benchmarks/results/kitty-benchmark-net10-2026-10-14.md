# Kitty rendering benchmark baseline

Measured on Windows 11 (10.0.26300.9550), .NET 10.0.13 x64, BenchmarkDotNet 0.15.2, using RyuJIT AVX2 and workstation concurrent GC. The benchmark used an eight-iteration job with five warmups and rendered through Avalonia's headless Skia CPU backend.

## Results

| Method | Mean | 99.9% confidence interval | StdDev | Managed allocation |
| --- | ---: | ---: | ---: | ---: |
| Render 741 unique Kitty images | 7.898 ms | 6.458–9.338 ms | 0.753 ms | 125.88 KB |
| Render 741 placements of one Kitty image | 7.445 ms | 6.526–8.365 ms | 0.481 ms | 138.17 KB |

The measurements do not show a reliable CPU-time difference because the confidence intervals overlap. The shared-image case allocated about 10% more managed memory per render in this run.

## Retained image pixel storage

Each unique image is 10×20 RGBA (800 bytes). The renderer-visible frame contains all 741 images in both cases:

| Workload | Unique decoded pixels | Visible bitmap pixel storage | Combined pixel storage |
| --- | ---: | ---: | ---: |
| 741 unique images | 592,800 B | 592,800 B | 1,185,600 B |
| 741 placements of one image | 800 B | 800 B | 1,600 B |

Sharing the image reduces these pixel-byte totals by 99.9% for this workload. These figures are calculated from pixel dimensions and the visible cache entries. They exclude bitmap metadata, allocator overhead, and backend or GPU texture storage; the headless CPU benchmark does not measure desktop GPU memory or execution time.

The workload and cache accounting are implemented in [`KittyGraphicsBenchmarks.cs`](../SvcSystems.UI.Terminal.Benchmarks/KittyGraphicsBenchmarks.cs). Re-run the benchmark before using these values to compare a later implementation or machine.
