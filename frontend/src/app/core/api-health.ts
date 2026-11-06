import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-config';

export interface ApiHealthStatusResponse {
  status: string;
}

@Service()
export class ApiHealth {
  private readonly http = inject(HttpClient);

  getStatus(): Observable<ApiHealthStatusResponse> {
    return this.http.get<ApiHealthStatusResponse>(`${API_BASE_URL}/health`);
  }
}
