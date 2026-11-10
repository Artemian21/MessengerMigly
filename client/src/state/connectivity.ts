import { create } from 'zustand'

export type HubState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting'

interface ConnectivityState {
  /** True while requests are slow or failing: free-tier instances sleep and take ~50 s to start. */
  waking: boolean
  hub: HubState
  setWaking: (waking: boolean) => void
  setHub: (hub: HubState) => void
}

export const useConnectivity = create<ConnectivityState>((set, get) => ({
  waking: false,
  hub: 'disconnected',
  setWaking: (waking) => {
    if (get().waking !== waking) set({ waking })
  },
  setHub: (hub) => set({ hub }),
}))
