// Rough C#-side throughput of Q16.16 fixed-point add/mul, same shape as
// game/scripts/bench_fixed_point.gd, for the GDScript-vs-C#-vs-GDExtension
// sizing call (inventory B7). Not a rigorous benchmark.
using System;
using System.Diagnostics;

const long Scale = 65536; // 2^16
const int Ops = 4_000_000;

long a = 3 * Scale;
long b = 2 * Scale;
long acc = 0;

var sw = Stopwatch.StartNew();
for (int i = 0; i < Ops; i++)
{
    acc = acc + a; // fixed-point add
    long product = (a * b) >> 16; // fixed-point mul
    acc = (acc + product) & 0xFFFFFFFF;
    a = (a + 1) & 0xFFFFF;
}
sw.Stop();

double elapsedSec = sw.Elapsed.TotalSeconds;
double opsPerSec = Ops / elapsedSec;
Console.WriteLine($"BENCH_CSHARP ops={Ops} elapsed_sec={elapsedSec:F4} ops_per_sec={opsPerSec:F0} acc={acc}");
