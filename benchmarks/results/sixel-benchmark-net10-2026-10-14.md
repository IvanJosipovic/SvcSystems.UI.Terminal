```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26300.9550)
Unknown processor
.NET SDK 11.0.100-rc.2.26504.105
  [Host]   : .NET 10.0.13 (10.0.1326.50504), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.13 (10.0.1326.50504), X64 RyuJIT AVX2

Job=ShortRun, 3 iterations, 1 launch, 3 warmups.

```
| Method                      | Mean         | Error         | StdDev       | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------- |-------------:|--------------:|-------------:|------:|--------:|-------:|----------:|------------:|
| DecodeAndRefreshViewport    | 485,837.8 ns | 278,183.39 ns | 15,248.17 ns | 1.001 | 0.04 | 0.9766 | 19281 B | 1.000 |
| CopyDecodedStripPixels      |   2,403.9 ns |   1,958.10 ns |    107.33 ns | 0.005 | 0.00 |      - |     - | 0.000 |
| CachedBitmapHit             |     130.1 ns |      42.98 ns |      2.36 ns | 0.000 | 0.00 | 0.0043 |    72 B | 0.004 |
| DecodeAndUploadChangedStrip | 513,171.3 ns | 311,298.39 ns | 17,063.32 ns | 1.057 | 0.04 | 0.9766 | 20705 B | 1.074 |
