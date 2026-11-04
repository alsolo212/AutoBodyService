import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface ApiHealthStatusResponse {
  status: string;
}

@Service()
export class ApiHealth {
  private readonly http = inject(HttpClient);

  getStatus(): Observable<ApiHealthStatusResponse> {
    return this.http.get<ApiHealthStatusResponse>('https://localhost:7097/api/health');
  }
}
