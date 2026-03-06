import type { IncidentsDashboardQuery } from './__generated__/IncidentsDashboardQuery.graphql'

export type IncidentItem = IncidentsDashboardQuery['response']['incidents'][number]
