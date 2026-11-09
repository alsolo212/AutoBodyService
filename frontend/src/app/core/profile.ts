import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { switchMap } from 'rxjs';

export interface OwnProfile { name: string; phone: string; }

@Injectable({ providedIn: 'root' })
export class ProfileService {
  private readonly http = inject(HttpClient);
  get() { return this.http.get<OwnProfile>('/api/profile'); }
  updateName(name: string) {
    return this.http.get<void>('/api/auth/antiforgery').pipe(
      switchMap(() => this.http.patch<void>('/api/profile', { name })),
    );
  }
}
