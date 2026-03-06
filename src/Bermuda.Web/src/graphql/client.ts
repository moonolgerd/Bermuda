import { Environment, Network, RecordSource, Store, type FetchFunction } from 'relay-runtime'

const apiUrl = import.meta.env.VITE_API_URL

const fetchFn: FetchFunction = async (request, variables) => {
  const response = await fetch(`${apiUrl}/graphql`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ query: request.text, variables }),
  })
  return response.json()
}

export const relayEnvironment = new Environment({
  network: Network.create(fetchFn),
  store: new Store(new RecordSource()),
})
