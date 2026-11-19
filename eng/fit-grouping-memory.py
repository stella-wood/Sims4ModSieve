#!/usr/bin/env python3
"""复算 #54 两项模型；仅用标准库读取合成测量，不访问 Mods。"""
import argparse
import csv
from decimal import Decimal, ROUND_CEILING
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'docs/research/conflict-grouping-memory-2026-11-19.csv'
MIB = 2**20
MIN_FIT_MATCHES = 100_000
PAIR_BYTES, MATCH_BYTES = 492, 124
MARGIN = Decimal('1.4')


def combinations(n):
    return n * (n - 1) // 2


def fit(points):
    # 过原点最小二乘 y=aP+bM；使用整数累加正规方程，最后才做除法。
    pp = sum(p * p for p, m, y in points)
    mm = sum(m * m for p, m, y in points)
    pm = sum(p * m for p, m, y in points)
    py = sum(p * y for p, m, y in points)
    my = sum(m * y for p, m, y in points)
    determinant = pp * mm - pm * pm
    return (py * mm - my * pm) / determinant, (my * pp - py * pm) / determinant


def estimate(pairs, matches):
    return int((Decimal(PAIR_BYTES * pairs + MATCH_BYTES * matches) * MARGIN)
               .to_integral_value(rounding=ROUND_CEILING))


def maximum(allowed):
    low, high = 0, 1
    while allowed(high):
        high *= 2
    while high - low > 1:
        middle = (low + high) // 2
        if allowed(middle):
            low = middle
        else:
            high = middle
    return low


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, help='另存逐次拟合残差 CSV')
    args = parser.parse_args()
    with SOURCE.open() as stream:
        rows = list(csv.DictReader(stream))
    points = []
    for row in rows:
        n = int(row['originals']) * int(row['copies'])
        p, m = combinations(n), int(row['matches'])
        # 合成样本的全局共享 TGI 使每个文件对都参与配对；逐行核对实物计数。
        assert p == int(row['pairs'])
        assert m == int(row['shared']) * p + (64 - int(row['shared'])) * int(row['originals'])
        points.append((p, m, int(row['sampledPeak']) - int(row['baseline'])))
    selected = [point for point in points if point[1] >= MIN_FIT_MATCHES]
    a, b = fit(selected)
    all_a, all_b = fit(points)
    print(f'M>={MIN_FIT_MATCHES}: {len(selected)} runs, a={a:.9f}, b={b:.9f}')
    print(f'All {len(points)} runs: a={all_a:.9f}, b={all_b:.9f}')
    large_a, large_b = fit([point for point in points if point[1] >= 500_000])
    print(f'M>=500000: a={large_a:.9f}, b={large_b:.9f}')
    print(f'Production: a={PAIR_BYTES}, b={MATCH_BYTES}, margin={MARGIN}')
    print('\n| U×2 | S/64 | 次 | P | M | 实测 MiB | 拟合 MiB | 残差 MiB（%） | 加余量估算 MiB |')
    print('| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')
    residuals = []
    for row, (p, m, actual) in zip(rows, points):
        predicted = a * p + b * m
        residual = predicted - actual
        charged = estimate(p, m)
        residuals.append(dict(originals=row['originals'], copies=row['copies'], shared=row['shared'],
                              repeat=row['repeat'], includedInFit=m >= MIN_FIT_MATCHES,
                              filePairUpperBound=p, resourceMatchCount=m, measuredPeakBytes=actual,
                              fittedPeakBytes=f'{predicted:.3f}', residualBytes=f'{residual:.3f}',
                              residualPercent=f'{100 * residual / actual:.6f}', estimatedMemoryBytes=charged))
        print(f"| {row['originals']} | {row['shared']} | {row['repeat']} | {p:,} | {m:,} | "
              f'{actual/MIB:.2f} | {predicted/MIB:.2f} | {residual/MIB:+.2f} ({100*residual/actual:+.1f}%) | {charged/MIB:.2f} |')
    if args.output:
        with args.output.open('w') as stream:
            writer = csv.DictWriter(stream, fieldnames=residuals[0].keys(), lineterminator="\n")
            writer.writeheader()
            writer.writerows(residuals)
    print('\n| 可用 GiB | 字节预算 GiB | #44 原包对数（文件数） | 48/64 无副本文件 | 1/64 无副本文件 |')
    print('| ---: | ---: | ---: | ---: | ---: |')
    for gib in (8, 16, 32):
        budget = gib * 2**30 // 4
        shapes = (lambda u: (combinations(2*u), 48*combinations(2*u)+16*u),
                  lambda n: (combinations(n), 48*combinations(n)),
                  lambda n: (combinations(n), combinations(n)))
        counts = [maximum(lambda n: estimate(*shape(n)) <= budget) for shape in shapes]
        # 验证整数边界：最大值放行，再多一个单位拦截。
        for count, shape in zip(counts, shapes):
            assert estimate(*shape(count)) <= budget < estimate(*shape(count+1))
        print(f'| {gib} | {gib//4} | {counts[0]:,}（{counts[0]*2:,}） | {counts[1]:,} | {counts[2]:,} |')
        if gib == 16:
            old_counts = [maximum(lambda n: shape(n)[1] <= budget//1024) for shape in shapes]
            print(f'<!-- 16 GiB 上一轮: {old_counts}; 本轮: {counts} -->')


if __name__ == '__main__':
    main()
