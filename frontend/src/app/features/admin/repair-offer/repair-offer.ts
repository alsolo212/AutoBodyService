import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, Input, OnChanges, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Observable, finalize } from 'rxjs';
import { ConfirmDialog } from '../../../shared/confirm-dialog/confirm-dialog';
import { ManualContactDialog } from '../manual-contact-dialog/manual-contact-dialog';
import { PauseOffer, PlanningOrder, PlanningOverview, RepairPlanningApi } from '../repair-planning-api';

@Component({ selector: 'app-repair-offer', imports: [RouterLink, ConfirmDialog, ManualContactDialog], templateUrl: './repair-offer.html' })
export class RepairOffer implements OnChanges {
  @Input() pauseId: string | null = null;
  private readonly api = inject(RepairPlanningApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly confirmation = viewChild.required(ConfirmDialog);
  private readonly contact = viewChild.required(ManualContactDialog);
  readonly candidate = signal<PlanningOrder | null>(null);
  readonly loading = signal(false); readonly saving = signal(false); readonly error = signal(''); readonly notice = signal('');
  readonly expectedReturn = signal<string | null>(null);
  private pending: PlanningOrder | null = null;
  ngOnChanges() { this.load(); }
  load() {
    this.loading.set(true); this.error.set('');
    const request: Observable<PauseOffer | PlanningOverview> = this.pauseId ? this.api.pauseOffer(this.pauseId) : this.api.overview();
    request.pipe(finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => { if ('candidate' in result) { this.candidate.set(result.candidate); this.expectedReturn.set(result.expectedReturn); } else this.candidate.set(result.earlyOpportunity); },
      error: () => { this.candidate.set(null); this.error.set('Не вдалося перевірити пропозиції.'); },
    });
  }
  call() { const item = this.candidate(); if (item) this.contact().open(item.id, 'Agreement', item); }
  decline() {
    const item = this.candidate(); if (!item || this.saving()) return;
    if (!this.pauseId) { this.candidate.set(null); this.notice.set('План залишено без змін.'); return; }
    this.saving.set(true); this.api.decline(this.pauseId, item.id).pipe(finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.notice.set('Відмову зафіксовано. Шукаємо наступний ремонт.'); this.load(); },
      error: () => { this.candidate.set(null); this.error.set('Пропозиція змінилася. Оновіть дані.'); },
    });
  }
  requestAccept() { const item = this.candidate(); if (item && !this.saving()) { this.pending = item; this.confirmation().open(); } }
  accept() {
    const item = this.pending; this.pending = null; if (!item || this.saving()) return;
    this.saving.set(true); this.api.accept(item, this.pauseId).pipe(finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.notice.set('Погоджену дату збережено.'); this.load(); },
      error: (error: unknown) => { this.error.set(error instanceof HttpErrorResponse && error.status === 409 ? 'Зайнятість або замовлення змінилися. Перевірте нову пропозицію.' : 'Не вдалося зберегти дату.'); this.candidate.set(null); },
    });
  }
}
