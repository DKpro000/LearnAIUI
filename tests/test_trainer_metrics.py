import unittest

import torch
from torch import nn
from torch.utils.data import DataLoader, TensorDataset

from trainer import evaluate_model


class IdentityLogitsModel(nn.Module):
    def forward(self, inputs):
        return inputs


class EvaluateModelMetricsTests(unittest.TestCase):
    def setUp(self):
        self.model = IdentityLogitsModel()
        self.device = torch.device("cpu")

    @staticmethod
    def make_loader(logits, targets, batch_size=2):
        dataset = TensorDataset(
            torch.tensor(logits, dtype=torch.float32),
            torch.tensor(targets, dtype=torch.long),
        )
        return DataLoader(dataset, batch_size=batch_size, shuffle=False)

    def test_macro_f1_and_confusion_matrix_are_correct(self):
        loader = self.make_loader(
            logits=[
                [4.0, 1.0, 0.0],  # target 0, predicts 0
                [0.0, 1.0, 4.0],  # target 1, predicts 2
                [0.0, 1.0, 4.0],  # target 2, predicts 2
                [0.0, 4.0, 1.0],  # target 2, predicts 1
            ],
            targets=[0, 1, 2, 2],
        )

        metrics = evaluate_model(
            model=self.model,
            data_loader=loader,
            input_shape=[1, 3],
            device=self.device,
            num_classes=3,
        )

        self.assertEqual(
            metrics["confusion_matrix"],
            [
                [1, 0, 0],
                [0, 0, 1],
                [0, 1, 1],
            ],
        )
        self.assertEqual(metrics["total_samples"], 4)
        self.assertAlmostEqual(metrics["accuracy"], 0.5)
        self.assertAlmostEqual(metrics["f1_macro"], 0.5)

    def test_rejects_model_output_width_that_differs_from_dataset_classes(self):
        loader = self.make_loader(
            logits=[[3.0, 1.0, 0.0], [0.0, 3.0, 1.0]],
            targets=[0, 1],
        )

        with self.assertRaisesRegex(
            RuntimeError,
            "Model output class count does not match the evaluation dataset",
        ):
            evaluate_model(
                model=self.model,
                data_loader=loader,
                input_shape=[1, 3],
                device=self.device,
                num_classes=2,
            )

    def test_rejects_target_outside_configured_class_range(self):
        loader = self.make_loader(
            logits=[[3.0, 1.0], [1.0, 3.0]],
            targets=[0, 2],
        )

        with self.assertRaisesRegex(
            RuntimeError,
            "Evaluation dataset contains a target outside the configured class range",
        ):
            evaluate_model(
                model=self.model,
                data_loader=loader,
                input_shape=[1, 2],
                device=self.device,
                num_classes=2,
            )


if __name__ == "__main__":
    unittest.main()
