/**
 * @generated SignedSource<<2128882f6449f6fb257459ec8626164c>>
 * @lightSyntaxTransform
 * @nogrep
 */

/* tslint:disable */
/* eslint-disable */
// @ts-nocheck

import { ConcreteRequest } from 'relay-runtime';
export type IncidentPriority = "CRITICAL" | "HIGH" | "LOW" | "MEDIUM" | "%future added value";
export type IncidentStatus = "CLOSED" | "INVESTIGATING" | "OPEN" | "%future added value";
export type IncidentsDashboardQuery$variables = Record<PropertyKey, never>;
export type IncidentsDashboardQuery$data = {
  readonly incidents: ReadonlyArray<{
    readonly date: any;
    readonly description: string;
    readonly id: string;
    readonly location: string;
    readonly priority: IncidentPriority;
    readonly status: IncidentStatus;
    readonly witnesses: number;
  }>;
};
export type IncidentsDashboardQuery = {
  response: IncidentsDashboardQuery$data;
  variables: IncidentsDashboardQuery$variables;
};

const node: ConcreteRequest = (function(){
var v0 = [
  {
    "alias": null,
    "args": null,
    "concreteType": "Incident",
    "kind": "LinkedField",
    "name": "incidents",
    "plural": true,
    "selections": [
      {
        "alias": null,
        "args": null,
        "kind": "ScalarField",
        "name": "id",
        "storageKey": null
      },
      {
        "alias": null,
        "args": null,
        "kind": "ScalarField",
        "name": "date",
        "storageKey": null
      },
      {
        "alias": null,
        "args": null,
        "kind": "ScalarField",
        "name": "location",
        "storageKey": null
      },
      {
        "alias": null,
        "args": null,
        "kind": "ScalarField",
        "name": "description",
        "storageKey": null
      },
      {
        "alias": null,
        "args": null,
        "kind": "ScalarField",
        "name": "status",
        "storageKey": null
      },
      {
        "alias": null,
        "args": null,
        "kind": "ScalarField",
        "name": "priority",
        "storageKey": null
      },
      {
        "alias": null,
        "args": null,
        "kind": "ScalarField",
        "name": "witnesses",
        "storageKey": null
      }
    ],
    "storageKey": null
  }
];
return {
  "fragment": {
    "argumentDefinitions": [],
    "kind": "Fragment",
    "metadata": null,
    "name": "IncidentsDashboardQuery",
    "selections": (v0/*: any*/),
    "type": "Query",
    "abstractKey": null
  },
  "kind": "Request",
  "operation": {
    "argumentDefinitions": [],
    "kind": "Operation",
    "name": "IncidentsDashboardQuery",
    "selections": (v0/*: any*/)
  },
  "params": {
    "cacheID": "acd5205e45b581db57d1d7d5791e8bea",
    "id": null,
    "metadata": {},
    "name": "IncidentsDashboardQuery",
    "operationKind": "query",
    "text": "query IncidentsDashboardQuery {\n  incidents {\n    id\n    date\n    location\n    description\n    status\n    priority\n    witnesses\n  }\n}\n"
  }
};
})();

(node as any).hash = "080d43a6d2c18b097f243bd33c19a29b";

export default node;
