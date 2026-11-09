import { Component, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { PublicContacts } from '../../../shared/public-contacts/public-contacts';
import { formatInspection } from '../booking/booking';

@Component({ selector: 'app-booking-success', imports: [RouterLink, PublicContacts], templateUrl: './booking-success.html', styleUrl: './booking-success.scss' })
export class BookingSuccess {
  private readonly router = inject(Router);
  readonly inspectionStartUtc: string | null = this.router.currentNavigation()?.extras.state?.['inspectionStartUtc'] ?? history.state?.inspectionStartUtc ?? null;
  readonly format = formatInspection;
}
