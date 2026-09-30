import { Injectable, computed, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';

export interface RegisterRequest {
  fullName: string;
  email: string;
  password: string;
}

export interface RegisterResponse {
  message?: string;
  userId?: string;
  email?: string;
  expiresAt?: string;
  otpExpiresAt?: string;
}

export interface VerifyEmailRequest {
  email: string;
  code: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface AuthResponse {
  message?: string;
  userId: string;
  fullName: string;
  email: string;
  role: string;
  token: string;
  expiresAt: string;
}

export interface AuthSession {
  userId: string;
  fullName: string;
  email: string;
  role: string;
  token: string;
  expiresAt: string;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly apiUrl =
    'http://localhost:5114/api/auth';

  private readonly storageKey =
    'gazetteai.auth.session';

  private readonly sessionState =
    signal<AuthSession | null>(this.readSession());

  readonly session = this.sessionState.asReadonly();

  readonly isAuthenticated = computed(() =>
    this.hasValidSession(this.sessionState())
  );

  readonly currentUser = computed(() => {
    const session = this.sessionState();

    return this.hasValidSession(session)
      ? session
      : null;
  });

  constructor(private readonly http: HttpClient) {}

  register(
    request: RegisterRequest
  ): Observable<RegisterResponse> {
    return this.http.post<RegisterResponse>(
      `${this.apiUrl}/register`,
      request
    );
  }

  verifyEmail(
    request: VerifyEmailRequest
  ): Observable<AuthResponse> {
    return this.http
      .post<AuthResponse>(
        `${this.apiUrl}/verify-email`,
        request
      )
      .pipe(tap(response => this.saveSession(response)));
  }

  resendOtp(email: string): Observable<{ message: string }> {
    return this.http.post<{ message: string }>(
      `${this.apiUrl}/resend-otp`,
      { email }
    );
  }

  login(
    request: LoginRequest
  ): Observable<AuthResponse> {
    return this.http
      .post<AuthResponse>(
        `${this.apiUrl}/login`,
        request
      )
      .pipe(tap(response => this.saveSession(response)));
  }

  logout(): void {
    localStorage.removeItem(this.storageKey);
    this.sessionState.set(null);
  }

  getToken(): string | null {
    return this.currentUser()?.token ?? null;
  }

  private saveSession(response: AuthResponse): void {
    const session: AuthSession = {
      userId: response.userId,
      fullName: response.fullName,
      email: response.email,
      role: response.role,
      token: response.token,
      expiresAt: response.expiresAt
    };

    localStorage.setItem(
      this.storageKey,
      JSON.stringify(session)
    );

    this.sessionState.set(session);
  }

  private readSession(): AuthSession | null {
    try {
      const stored = localStorage.getItem(this.storageKey);

      if (!stored) {
        return null;
      }

      const session = JSON.parse(stored) as AuthSession;

      if (!this.hasValidSession(session)) {
        localStorage.removeItem(this.storageKey);
        return null;
      }

      return session;
    } catch {
      localStorage.removeItem(this.storageKey);
      return null;
    }
  }

  private hasValidSession(
    session: AuthSession | null
  ): session is AuthSession {
    if (!session?.token || !session.userId) {
      return false;
    }

    const expiry = Date.parse(session.expiresAt);

    return Number.isFinite(expiry) && expiry > Date.now();
  }
}
