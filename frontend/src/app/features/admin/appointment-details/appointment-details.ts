import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AppointmentDetails, statusLabel, price } from '../../appointments/appointments-api';
import { kyivTime } from '../../../shared/time/kyiv-time';

interface AdminDetails extends AppointmentDetails { customerName: string; customerPhone: string; }
@Component({ selector: 'app-admin-appointment-details', imports: [RouterLink], templateUrl: './appointment-details.html', styleUrl: './appointment-details.scss' })
export class AppointmentDetailsAdmin {
  private readonly http = inject(HttpClient);
  private readonly id = inject(ActivatedRoute).snapshot.paramMap.get('id') ?? '';
  readonly item = signal<AdminDetails | null>(null); readonly loading = signal(true); readonly failed = signal(false);
  readonly format = kyivTime; readonly status = statusLabel; readonly price = price;
  constructor() { this.load(); }
  load() { this.loading.set(true); this.failed.set(false); this.http.get<AdminDetails>(`/api/appointments/${encodeURIComponent(this.id)}`).subscribe({ next: item => { this.item.set(item); this.loading.set(false); }, error: () => { this.failed.set(true); this.loading.set(false); } }); }
}
