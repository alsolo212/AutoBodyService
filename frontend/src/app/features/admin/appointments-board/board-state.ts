import { Injectable, effect, inject } from '@angular/core';
import { AuthService } from '../../../core/auth';
import { AppointmentStatus } from '../../appointments/appointments-api';
import { AdminBoardFilter } from '../appointment-details/admin-appointment-api';

export type StatusFilter = AppointmentStatus | '';
export interface BoardSnapshot {
  filter: AdminBoardFilter;
  status: StatusFilter;
  draft: AdminBoardFilter & { status: StatusFilter };
  pages: Partial<Record<AppointmentStatus, number>>;
  scrollY: number;
  scrollLeft: number;
  filtersExpanded: boolean;
}

@Injectable({ providedIn: 'root' })
export class BoardState {
  private readonly auth = inject(AuthService);
  private saved: { sessionVersion: number; detailsPath: string; snapshot: BoardSnapshot } | null = null;

  constructor() {
    effect(() => {
      const session = this.auth.session();
      if (!session || (this.saved && this.saved.sessionVersion !== this.auth.sessionVersion())) this.saved = null;
    });
  }

  save(snapshot: BoardSnapshot, detailsPath: string): void { this.saved = { sessionVersion: this.auth.sessionVersion(), detailsPath, snapshot }; }

  take(previousPath: string): BoardSnapshot | null {
    const saved = this.saved;
    this.saved = null;
    return saved?.detailsPath === previousPath && saved.sessionVersion === this.auth.sessionVersion() ? saved.snapshot : null;
  }
}
