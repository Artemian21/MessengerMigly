import { create } from 'zustand'
import type { AuthResponse, Me } from '../api/types'

type Status = 'loading' | 'authenticated' | 'anonymous'

interface AuthState {
  /** Kept in memory only (NFR-02); a reload bootstraps through the refresh cookie. */
  accessToken: string | null
  expiresAt: number
  user: Me | null
  status: Status
  signedIn: (response: AuthResponse) => void
  setUser: (user: Me) => void
  signedOut: () => void
  isExpiringSoon: () => boolean
}

export const useAuth = create<AuthState>((set, get) => ({
  accessToken: null,
  expiresAt: 0,
  user: null,
  status: 'loading',
  signedIn: (response) =>
    set({
      accessToken: response.accessToken,
      expiresAt: Date.parse(response.expiresAt),
      user: response.user,
      status: 'authenticated',
    }),
  setUser: (user) => set({ user }),
  signedOut: () => set({ accessToken: null, expiresAt: 0, user: null, status: 'anonymous' }),
  isExpiringSoon: () => get().expiresAt - Date.now() < 60_000,
}))
