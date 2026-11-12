import { HttpClient } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize } from 'rxjs';
import { API_BASE_URL } from '../../core/api-config';

interface PublicPrice { id: string; name: string; description: string; priceExact: string; currency: string; isFrom: boolean; }

@Component({ selector: 'app-public-prices', templateUrl: './public-prices.html', styleUrl: './public-prices.scss' })
export class PublicPrices {
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);
  readonly items = signal<PublicPrice[]>([]);
  readonly loading = signal(false);
  readonly failed = signal(false);
  constructor() { this.load(); }
  load() {
    if (this.loading()) return;
    this.loading.set(true); this.failed.set(false);
    this.http.get<PublicPrice[]>(`${API_BASE_URL}/prices`).pipe(
      finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroyRef),
    ).subscribe({ next: items => this.items.set(items), error: () => { this.items.set([]); this.failed.set(true); } });
  }
  amount(item: PublicPrice) { return `${item.isFrom ? 'від ' : ''}${item.priceExact.replace('.', ',')} ₴`; }
}
