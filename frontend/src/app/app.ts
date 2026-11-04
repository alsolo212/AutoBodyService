import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ApiHealth } from './core/api-health';

@Component({
  imports: [RouterOutlet],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App implements OnInit {
  private readonly apiHealth = inject(ApiHealth);

  protected readonly apiStatus = signal('Перевіряємо з’єднання з API…');

  ngOnInit(): void {
    this.apiHealth.getStatus().subscribe({
      next: (response) => this.apiStatus.set(`API: ${response.status}`),
      error: () => this.apiStatus.set('Не вдалося з’єднатися з API.'),
    });
  }
}
